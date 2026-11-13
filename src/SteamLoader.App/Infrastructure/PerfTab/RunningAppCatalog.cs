using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SteamLoader.App.Infrastructure.Handheld;

namespace SteamLoader.App.Infrastructure.PerfTab;

/// <summary>A game or app the user can add a profile for.</summary>
public sealed record PerfTabRunningApp(string Key, string Title, string ExecutablePath, string AppId, bool IsCurrentGame, bool HasProfile);

/// <summary>
/// Open windows the user may pick in "add profile": one entry per program, without the
/// Windows shell, Steam and TFS itself.
/// </summary>
internal static class RunningAppCatalog
{
    private const int GwOwner = 4;
    private const int DwmwaCloaked = 14;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static readonly HashSet<string> HiddenProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "TextInputHost",
        "ApplicationFrameHost", "LockApp", "Widgets", "Idle", "System", "dwm", "steam", "steamwebhelper",
        "ToolsForSteam", "GameBar", "GameBarFTServer", "SystemSettings", "ShellHost",
        "NVIDIA Overlay", "NVIDIA Share", "RadeonSoftware", "AMDRSServ", "CssLoader-Standalone-Headless",
    };

    public static IReadOnlyList<(string Key, string Title, string ExecutablePath)> ListWindows()
    {
        var found = new Dictionary<string, (string Key, string Title, string ExecutablePath)>(StringComparer.OrdinalIgnoreCase);
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        EnumWindows((handle, unused) =>
        {
            try
            {
                if (!IsWindowVisible(handle) || GetWindow(handle, GwOwner) != IntPtr.Zero || IsCloaked(handle))
                {
                    return true;
                }

                var title = WindowTitle(handle);
                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                _ = GetWindowThreadProcessId(handle, out var processId);
                if (processId == 0 || processId == Environment.ProcessId)
                {
                    return true;
                }

                using var process = Process.GetProcessById((int)processId);
                if (HiddenProcesses.Contains(process.ProcessName))
                {
                    return true;
                }

                var path = ExecutablePath(processId);
                if (string.IsNullOrWhiteSpace(path) ||
                    (!string.IsNullOrEmpty(windowsDirectory) && path.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                var key = HybridGameProcessMonitor.KeyForExecutable(path);
                if (key.Length > 0 && !found.ContainsKey(key))
                {
                    found[key] = (key, title.Trim(), path);
                }
            }
            catch
            {
                // A window that closes while we look at it is simply skipped.
            }

            return true;
        }, IntPtr.Zero);

        return found.Values.OrderBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool IsCloaked(IntPtr handle)
        => DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string WindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLengthW(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowTextW(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string ExecutablePath(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            var builder = new StringBuilder(1024);
            var size = builder.Capacity;
            return QueryFullProcessImageNameW(process, 0, builder, ref size) ? builder.ToString(0, size) : string.Empty;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
