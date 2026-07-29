using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.Processes;

public sealed class ProcessWindowService
{
    private static readonly TimeSpan LaunchedAppFocusTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LaunchedAppPollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ExistingNameMatchDelay = TimeSpan.FromMilliseconds(750);

    private static readonly HashSet<string> IgnoredClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Progman",
        "WorkerW",
        "DV2ControlHost",
        "MsgrIMEWindowClass",
        "SysShadow",
        "NotifyIconOverflowWindow",
    };

    private static readonly HashSet<string> IgnoredProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost",
        "TextInputHost",
        "StartMenuExperienceHost",
        "ShellExperienceHost",
        "Widgets",
        "SearchHost",
        "LockApp",
    };

    private static readonly HashSet<string> GenericLaunchHostProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd",
        "cscript",
        "explorer",
        "msiexec",
        "powershell",
        "pwsh",
        "rundll32",
        "wscript",
    };

    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "arc",
        "brave",
        "chrome",
        "firefox",
        "floorp",
        "librewolf",
        "msedge",
        "opera",
        "operagx",
        "vivaldi",
        "waterfox",
        "zen",
    };

    private int _launchedAppFocusGeneration;
    private int _urlHandlerFocusGeneration;

    public ProcessesSnapshot GetSnapshot()
    {
        var windows = EnumerateWindows()
            .OrderByDescending(window => window.IsForeground)
            .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var statusText = windows.Count switch
        {
            0 => "No open app windows detected.",
            1 => "1 open app window ready.",
            _ => $"{windows.Count} open app windows ready.",
        };

        return new ProcessesSnapshot(windows, statusText);
    }

    public ProcessesSnapshot ActivateWindow(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            throw new InvalidOperationException("A target window handle is required.");
        }

        if (!TryParseHandle(handle, out var targetWindow))
        {
            throw new InvalidOperationException("The selected window handle is invalid.");
        }

        if (!IsWindow(targetWindow))
        {
            throw new InvalidOperationException("The selected window is no longer available.");
        }

        FocusWindow(targetWindow);
        return GetSnapshot();
    }

    public bool IsForegroundWindow(string handle) =>
        TryParseHandle(handle, out var windowHandle) &&
        IsWindow(windowHandle) &&
        GetForegroundWindow() == windowHandle;

    public void ActivateLaunchedAppWhenReady(
        string appName,
        string? expectedProcessName,
        int? launchedProcessId,
        IReadOnlyList<ProcessWindowInfo> windowsBeforeLaunch)
    {
        var generation = Interlocked.Increment(ref _launchedAppFocusGeneration);
        _ = ActivateLaunchedAppWhenReadyAsync(
            appName,
            expectedProcessName,
            launchedProcessId,
            windowsBeforeLaunch,
            generation);
    }

    public void ActivateUrlHandlerWhenReady(IReadOnlyList<ProcessWindowInfo> windowsBeforeLaunch)
    {
        var generation = Interlocked.Increment(ref _urlHandlerFocusGeneration);
        _ = ActivateUrlHandlerWhenReadyAsync(windowsBeforeLaunch, generation);
    }

    private async Task ActivateUrlHandlerWhenReadyAsync(
        IReadOnlyList<ProcessWindowInfo> windowsBeforeLaunch,
        int generation)
    {
        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var deadline = startedAt + LaunchedAppFocusTimeout;
            while (DateTimeOffset.UtcNow < deadline &&
                   Volatile.Read(ref _urlHandlerFocusGeneration) == generation)
            {
                var candidate = SelectUrlHandlerWindow(
                    windowsBeforeLaunch,
                    GetSnapshot().Windows,
                    allowExistingWindow: DateTimeOffset.UtcNow - startedAt >= ExistingNameMatchDelay);
                if (candidate is not null)
                {
                    if (!candidate.IsForeground)
                    {
                        try
                        {
                            ActivateWindow(candidate.Handle);
                        }
                        catch (InvalidOperationException)
                        {
                            await Task.Delay(LaunchedAppPollInterval).ConfigureAwait(false);
                            continue;
                        }
                    }

                    return;
                }

                await Task.Delay(LaunchedAppPollInterval).ConfigureAwait(false);
            }
        }
        catch
        {
            // The URL launch itself remains successful when Windows refuses a
            // foreground handoff or the configured handler has no normal window.
        }
    }

    private async Task ActivateLaunchedAppWhenReadyAsync(
        string appName,
        string? expectedProcessName,
        int? launchedProcessId,
        IReadOnlyList<ProcessWindowInfo> windowsBeforeLaunch,
        int generation)
    {
        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var deadline = startedAt + LaunchedAppFocusTimeout;
            while (DateTimeOffset.UtcNow < deadline &&
                   Volatile.Read(ref _launchedAppFocusGeneration) == generation)
            {
                var windows = GetSnapshot().Windows;
                var candidate = SelectLaunchedAppWindow(
                    appName,
                    expectedProcessName,
                    launchedProcessId,
                    windowsBeforeLaunch,
                    windows,
                    allowExistingNameMatch: DateTimeOffset.UtcNow - startedAt >= ExistingNameMatchDelay);
                if (candidate is not null)
                {
                    if (candidate.IsForeground)
                    {
                        return;
                    }

                    if (Volatile.Read(ref _launchedAppFocusGeneration) != generation)
                    {
                        return;
                    }

                    try
                    {
                        ActivateWindow(candidate.Handle);
                        return;
                    }
                    catch (InvalidOperationException)
                    {
                        // Splash and launcher windows can disappear while they
                        // hand off to the real app. Keep polling for its window.
                    }
                }

                await Task.Delay(LaunchedAppPollInterval).ConfigureAwait(false);
            }
        }
        catch
        {
            // Starting an app must remain successful even when it deliberately
            // has no visible window or Windows refuses the foreground handoff.
        }
    }

    internal static ProcessWindowInfo? SelectLaunchedAppWindow(
        string appName,
        string? expectedProcessName,
        int? launchedProcessId,
        IReadOnlyList<ProcessWindowInfo> windowsBeforeLaunch,
        IReadOnlyList<ProcessWindowInfo> currentWindows,
        bool allowExistingNameMatch = true)
    {
        if (launchedProcessId is > 0)
        {
            var launchedProcessWindow = currentWindows
                .Where(window =>
                    window.ProcessId == launchedProcessId.Value &&
                    !IsGenericLaunchHostProcess(window.ProcessName))
                .OrderByDescending(window => window.IsForeground)
                .FirstOrDefault();
            if (launchedProcessWindow is not null)
            {
                return launchedProcessWindow;
            }
        }

        var normalizedExpectedProcessName = NormalizeAppIdentifier(expectedProcessName);
        if (normalizedExpectedProcessName.Length >= 3)
        {
            var expectedProcessWindow = currentWindows
                .Where(window =>
                    !IsGenericLaunchHostProcess(window.ProcessName) &&
                    ProcessNameMatches(window.ProcessName, normalizedExpectedProcessName))
                .OrderByDescending(window => window.IsForeground)
                .FirstOrDefault();
            if (expectedProcessWindow is not null)
            {
                return expectedProcessWindow;
            }
        }

        var previousHandles = windowsBeforeLaunch
            .Select(window => window.Handle)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newWindow = currentWindows
            .Where(window => !previousHandles.Contains(window.Handle))
            .Select(window => new { Window = window, Score = ScoreAppNameMatch(appName, window) })
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Window.IsForeground)
            .Select(candidate => candidate.Window)
            .FirstOrDefault();
        if (newWindow is not null)
        {
            return newWindow;
        }

        var previousForegroundHandles = windowsBeforeLaunch
            .Where(window => window.IsForeground)
            .Select(window => window.Handle)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changedForegroundWindow = currentWindows.FirstOrDefault(window =>
            window.IsForeground &&
            !previousForegroundHandles.Contains(window.Handle) &&
            !IsTfsOrSteamProcess(window.ProcessName));
        if (changedForegroundWindow is not null || !allowExistingNameMatch)
        {
            return changedForegroundWindow;
        }

        return currentWindows
            .Select(window => new { Window = window, Score = ScoreAppNameMatch(appName, window) })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Window.IsForeground)
            .Select(candidate => candidate.Window)
            .FirstOrDefault();
    }

    internal static ProcessWindowInfo? SelectUrlHandlerWindow(
        IReadOnlyList<ProcessWindowInfo> windowsBeforeLaunch,
        IReadOnlyList<ProcessWindowInfo> currentWindows,
        bool allowExistingWindow = true)
    {
        var previousByHandle = windowsBeforeLaunch.ToDictionary(
            window => window.Handle,
            StringComparer.OrdinalIgnoreCase);
        var browserWindows = currentWindows
            .Where(window => IsBrowserProcess(window.ProcessName))
            .ToArray();

        var changedBrowser = browserWindows
            .Where(window =>
                !previousByHandle.TryGetValue(window.Handle, out var previous) ||
                !window.Title.Equals(previous.Title, StringComparison.Ordinal))
            .OrderByDescending(window => window.IsForeground)
            .FirstOrDefault();
        if (changedBrowser is not null)
        {
            return changedBrowser;
        }

        var newlyForegroundBrowser = browserWindows.FirstOrDefault(window =>
            window.IsForeground &&
            (!previousByHandle.TryGetValue(window.Handle, out var previous) || !previous.IsForeground));
        if (newlyForegroundBrowser is not null || !allowExistingWindow)
        {
            return newlyForegroundBrowser;
        }

        return browserWindows.Length == 1
            ? browserWindows[0]
            : browserWindows.FirstOrDefault(window => window.IsForeground);
    }

    private static bool ProcessNameMatches(string processName, string normalizedExpectedProcessName)
    {
        var normalizedProcessName = NormalizeAppIdentifier(processName);
        return normalizedProcessName.Equals(normalizedExpectedProcessName, StringComparison.OrdinalIgnoreCase) ||
               normalizedProcessName.Length >= 4 &&
               normalizedExpectedProcessName.Length >= 4 &&
               (normalizedProcessName.Contains(normalizedExpectedProcessName, StringComparison.OrdinalIgnoreCase) ||
                normalizedExpectedProcessName.Contains(normalizedProcessName, StringComparison.OrdinalIgnoreCase));
    }

    private static int ScoreAppNameMatch(string appName, ProcessWindowInfo window)
    {
        var normalizedAppName = NormalizeAppIdentifier(appName);
        if (normalizedAppName.Length < 3)
        {
            return 0;
        }

        var normalizedTitle = NormalizeAppIdentifier(window.Title);
        var normalizedProcessName = NormalizeAppIdentifier(window.ProcessName);
        if (normalizedProcessName.Equals(normalizedAppName, StringComparison.OrdinalIgnoreCase))
        {
            return 600;
        }

        if (normalizedTitle.Equals(normalizedAppName, StringComparison.OrdinalIgnoreCase))
        {
            return 550;
        }

        if (normalizedTitle.Contains(normalizedAppName, StringComparison.OrdinalIgnoreCase))
        {
            return 500;
        }

        if (normalizedProcessName.Length >= 4 &&
            (normalizedProcessName.Contains(normalizedAppName, StringComparison.OrdinalIgnoreCase) ||
             normalizedAppName.Contains(normalizedProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            return 450;
        }

        return 0;
    }

    private static string NormalizeAppIdentifier(string? value) =>
        string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static bool IsTfsOrSteamProcess(string processName) =>
        processName.Equals("ToolsForSteam", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("SteamLoader", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("steam", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase);

    private static bool IsGenericLaunchHostProcess(string processName) =>
        GenericLaunchHostProcessNames.Contains(NormalizeAppIdentifier(processName));

    private static bool IsBrowserProcess(string processName) =>
        BrowserProcessNames.Contains(NormalizeAppIdentifier(processName));

    internal static bool IsAllowedHostedAppWindow(
        string processName,
        string title) =>
        processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) &&
        title.Trim().Equals("XBOX", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ProcessWindowInfo> EnumerateWindows()
    {
        var windows = new List<ProcessWindowInfo>();
        var shellWindow = GetShellWindow();
        var foregroundWindow = GetForegroundWindow();
        var handle = GCHandle.Alloc(new EnumerationContext(windows, shellWindow, foregroundWindow));
        try
        {
            EnumWindows(
                static (windowHandle, parameter) =>
                {
                    var context = (EnumerationContext)GCHandle.FromIntPtr(parameter).Target!;
                    context.CollectWindow(windowHandle);
                    return true;
                },
                GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return windows;
    }

    private static void FocusWindow(nint windowHandle)
    {
        if (IsIconic(windowHandle))
        {
            ShowWindow(windowHandle, ShowWindowRestore);
        }
        else
        {
            ShowWindow(windowHandle, ShowWindowShow);
        }

        if (TryFocusWindow(windowHandle))
        {
            return;
        }

        // Windows normally prevents a background process from stealing focus.
        // A short ALT input grants the caller the same foreground permission as
        // an interactive window switch without leaving a modifier held down.
        KeybdEvent(VirtualKeyMenu, 0, 0, 0);
        try
        {
            if (TryFocusWindow(windowHandle))
            {
                return;
            }
        }
        finally
        {
            KeybdEvent(VirtualKeyMenu, 0, KeyEventKeyUp, 0);
        }

        SwitchToThisWindow(windowHandle, true);
        Thread.Sleep(75);
        if (GetForegroundWindow() == windowHandle)
        {
            return;
        }

        // Last-resort z-order pulse for SDL/Chromium windows such as Steam Big
        // Picture. The window is immediately returned to normal (non-topmost)
        // state after it has been brought above the current foreground window.
        SetWindowPos(windowHandle, WindowTopMost, 0, 0, 0, 0, SetWindowPositionFlags);
        SetWindowPos(windowHandle, WindowNotTopMost, 0, 0, 0, 0, SetWindowPositionFlags);
        if (!TryFocusWindow(windowHandle))
        {
            throw new InvalidOperationException("Windows did not grant foreground focus to the selected window.");
        }
    }

    private static bool TryFocusWindow(nint windowHandle)
    {
        if (GetForegroundWindow() == windowHandle)
        {
            return true;
        }

        var foregroundWindow = GetForegroundWindow();
        var currentThreadId = GetCurrentThreadId();
        var foregroundThreadId = foregroundWindow != 0
            ? GetWindowThreadProcessId(foregroundWindow, out _)
            : 0;
        var targetThreadId = GetWindowThreadProcessId(windowHandle, out _);

        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            if (targetThreadId != 0 &&
                targetThreadId != currentThreadId &&
                targetThreadId != foregroundThreadId)
            {
                AttachThreadInput(currentThreadId, targetThreadId, true);
            }

            SetWindowPos(windowHandle, WindowTop, 0, 0, 0, 0, SetWindowPositionFlags);
            BringWindowToTop(windowHandle);
            SetActiveWindow(windowHandle);
            SetForegroundWindow(windowHandle);
            SetFocus(windowHandle);
            Thread.Sleep(50);
            return GetForegroundWindow() == windowHandle;
        }
        finally
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }

            if (targetThreadId != 0 &&
                targetThreadId != currentThreadId &&
                targetThreadId != foregroundThreadId)
            {
                AttachThreadInput(currentThreadId, targetThreadId, false);
            }
        }
    }

    private static bool TryParseHandle(string rawValue, out nint handle)
    {
        if (rawValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(rawValue[2..], System.Globalization.NumberStyles.HexNumber, null, out var hexValue))
        {
            handle = new nint(hexValue);
            return true;
        }

        if (long.TryParse(rawValue, out var decimalValue))
        {
            handle = new nint(decimalValue);
            return true;
        }

        handle = 0;
        return false;
    }

    private sealed class EnumerationContext
    {
        private readonly List<ProcessWindowInfo> _windows;
        private readonly nint _shellWindow;
        private readonly nint _foregroundWindow;

        public EnumerationContext(List<ProcessWindowInfo> windows, nint shellWindow, nint foregroundWindow)
        {
            _windows = windows;
            _shellWindow = shellWindow;
            _foregroundWindow = foregroundWindow;
        }

        public void CollectWindow(nint windowHandle)
        {
            if (windowHandle == 0 || windowHandle == _shellWindow)
            {
                return;
            }

            if (!IsWindowVisible(windowHandle))
            {
                return;
            }

            if (IsWindowCloaked(windowHandle))
            {
                return;
            }

            var className = GetWindowClassName(windowHandle);
            if (IgnoredClassNames.Contains(className))
            {
                return;
            }

            var title = GetWindowTitle(windowHandle);
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            var extendedStyle = GetWindowLongPtr(windowHandle, GetWindowLongExStyle).ToInt64();
            var owner = GetWindow(windowHandle, GetWindowOwner);
            var isToolWindow = (extendedStyle & WindowExStyleToolWindow) != 0;
            var hasAppWindowFlag = (extendedStyle & WindowExStyleAppWindow) != 0;

            if (isToolWindow)
            {
                return;
            }

            if (owner != 0 && !hasAppWindowFlag)
            {
                return;
            }

            if (!TryGetWindowRect(windowHandle, out var rectangle))
            {
                return;
            }

            var width = rectangle.Right - rectangle.Left;
            var height = rectangle.Bottom - rectangle.Top;
            if (width < 140 || height < 90)
            {
                return;
            }

            GetWindowThreadProcessId(windowHandle, out var nativeProcessId);
            var processId = unchecked((int)nativeProcessId);
            if (processId <= 0)
            {
                return;
            }

            string processName;
            try
            {
                using var process = Process.GetProcessById(processId);
                processName = process.ProcessName;
            }
            catch
            {
                return;
            }

            if (IgnoredProcessNames.Contains(processName) &&
                !IsAllowedHostedAppWindow(processName, title))
            {
                return;
            }

            _windows.Add(new ProcessWindowInfo(
                Handle: $"0x{windowHandle.ToInt64():X}",
                Title: title.Trim(),
                ProcessName: processName,
                ProcessId: processId,
                IsMinimized: IsIconic(windowHandle),
                IsForeground: windowHandle == _foregroundWindow));
        }

        private static bool TryGetWindowRect(nint windowHandle, out Rect rectangle)
        {
            if (GetWindowRect(windowHandle, out rectangle))
            {
                return true;
            }

            rectangle = default;
            return false;
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

        private static string GetWindowClassName(nint windowHandle)
        {
            var builder = new StringBuilder(256);
            return GetClassNameW(windowHandle, builder, builder.Capacity) > 0
                ? builder.ToString()
                : string.Empty;
        }

        private static bool IsWindowCloaked(nint windowHandle)
        {
            if (DwmGetWindowAttribute(windowHandle, DwmwaCloaked, out var cloaked, sizeof(int)) != 0)
            {
                return false;
            }

            return cloaked != 0;
        }
    }

    private const int DwmwaCloaked = 14;
    private const int GetWindowLongExStyle = -20;
    private const int GetWindowOwner = 4;
    private const int ShowWindowRestore = 9;
    private const int ShowWindowShow = 5;
    private const byte VirtualKeyMenu = 0x12;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint SetWindowPositionFlags = 0x0001 | 0x0002 | 0x0040;
    private static readonly nint WindowTop = 0;
    private static readonly nint WindowTopMost = -1;
    private static readonly nint WindowNotTopMost = -2;
    private const long WindowExStyleToolWindow = 0x00000080L;
    private const long WindowExStyleAppWindow = 0x00040000L;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(nint windowHandle, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(nint windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint windowHandle, StringBuilder builder, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint windowHandle, StringBuilder builder, int count);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint windowHandle, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint windowHandle, out Rect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachingThreadId, uint attachedThreadId, bool attach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(nint windowHandle, bool altTab);

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);
}
