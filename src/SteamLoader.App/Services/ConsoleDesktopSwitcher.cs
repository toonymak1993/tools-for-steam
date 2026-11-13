using System.Runtime.InteropServices;

namespace SteamLoader.App.Services;

/// <summary>
/// "Start Windows Desktop" for the console modes: make sure Explorer runs,
/// show the taskbar, step Big Picture aside and tell the taskbar guard that the
/// user is on the desktop until Big Picture comes back to the front.
/// </summary>
internal static class ConsoleDesktopSwitcher
{
    private const int ShowWindowMinimize = 6;

    public static void SwitchToDesktop(
        WindowsShellService shellService,
        WindowsShellVisibilityService visibilityService,
        ConsoleShellState state,
        bool minimizeBigPicture)
    {
        // Explorer normally runs already; start it if it crashed or was closed.
        shellService.StartWindowsShellIfNeeded();
        state.RequestDesktop(DateTimeOffset.UtcNow);
        visibilityService.RestoreShellChrome();

        var minimized = 0;
        if (minimizeBigPicture)
        {
            foreach (var handle in SteamBigPictureForegroundDetector.FindGamepadUiWindows())
            {
                // Async: a busy Steam window must never block the caller.
                if (ShowWindowAsync(handle, ShowWindowMinimize))
                {
                    minimized++;
                }
            }
        }

        SteamStartupDiagnostics.Write(
            $"console desktop session requested (bigPictureWindowsMinimized={minimized})");
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);
}
