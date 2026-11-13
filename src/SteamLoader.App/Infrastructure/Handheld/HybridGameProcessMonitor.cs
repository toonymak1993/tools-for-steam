using System.Diagnostics;
using SteamLoader.App.Infrastructure.Performance;

namespace SteamLoader.App.Infrastructure.Handheld;

internal sealed class HybridGameProcessMonitor
{
    private readonly SteamGameProcessMonitor? _steamMonitor;
    private readonly Func<ForegroundTargetCandidate?> _resolveForegroundTarget;
    private readonly Func<int, bool> _isProcessAlive;
    private HandheldRunningGame? _lastNonSteamGame;

    private readonly Func<ForegroundTargetCandidate, bool> _isFullscreen;

    // Desktop apps that open big windows but are never games (game profiles must not follow them).
    private static readonly HashSet<string> NonGameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "arc",
        "Claude", "ChatGPT", "Discord", "Spotify", "Teams", "ms-teams", "slack", "Zoom", "Telegram", "WhatsApp",
        "SnippingTool", "ScreenSketch", "notepad", "Notepad", "mspaint", "SystemSettings", "Taskmgr", "mmc",
        "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "olk", "ONENOTE", "Acrobat", "AcroRd32",
        "obs64", "vlc", "Photos", "Microsoft.Photos", "Video.UI", "devenv", "rider64", "idea64", "cmd", "conhost",
        "AYASpace", "ArmouryCrate", "LegionSpace", "Playnite.DesktopApp", "Playnite.FullscreenApp",
        "EpicGamesLauncher", "GalaxyClient", "EADesktop", "UbisoftConnect", "Battle.net", "XboxPcApp", "GameBar",
    };

    // Install folders of game stores: anything started from here counts even when windowed.
    private static readonly string[] GameFolderMarkers =
    [
        @"\steamapps\common\", @"\XboxGames\", @"\Epic Games\", @"\GOG Galaxy\Games\", @"\GOG Games\",
        @"\EA Games\", @"\Ubisoft Game Launcher\games\", @"\Battle.net\", @"\Riot Games\",
    ];

    private readonly Func<string, bool> _hasProfile;

    public HybridGameProcessMonitor(string? steamRootPath, Func<string, bool>? hasProfile = null)
        : this(
            string.IsNullOrWhiteSpace(steamRootPath) ? null : new SteamGameProcessMonitor(steamRootPath),
            PerformanceForegroundTargetResolver.TryResolve,
            IsProcessAlive,
            IsFullscreenWindow,
            hasProfile)
    {
    }

    internal HybridGameProcessMonitor(
        SteamGameProcessMonitor? steamMonitor,
        Func<ForegroundTargetCandidate?> resolveForegroundTarget,
        Func<int, bool> isProcessAlive,
        Func<ForegroundTargetCandidate, bool>? isFullscreen = null,
        Func<string, bool>? hasProfile = null)
    {
        _steamMonitor = steamMonitor;
        _resolveForegroundTarget = resolveForegroundTarget;
        _isProcessAlive = isProcessAlive;
        _isFullscreen = isFullscreen ?? (_ => true);
        _hasProfile = hasProfile ?? (_ => false);
    }

    /// <summary>Profile key of an executable, shared by the monitor and "add profile".</summary>
    internal static string KeyForExecutable(string executablePath)
    {
        var normalized = NormalizeExecutablePath(executablePath);
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : $"exe:{normalized.ToLowerInvariant()}";
    }

    /// <summary>
    /// A non-Steam foreground window is treated as a game when it was started from a game
    /// store folder, or when it fills its monitor and is not a known desktop app.
    /// </summary>
    internal static bool LooksLikeGame(ForegroundTargetCandidate target, Func<ForegroundTargetCandidate, bool> isFullscreen)
    {
        if (NonGameProcesses.Contains(target.ProcessName))
        {
            return false;
        }

        var path = target.ExecutablePath ?? string.Empty;
        if (GameFolderMarkers.Any(marker => path.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return isFullscreen(target);
    }

    private static bool IsFullscreenWindow(ForegroundTargetCandidate target)
    {
        var text = target.WindowHandle?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true
            ? target.WindowHandle[2..]
            : target.WindowHandle;
        if (!long.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var raw) || raw == 0)
        {
            return false;
        }

        var handle = new IntPtr(raw);
        if (!GetWindowRect(handle, out var window))
        {
            return false;
        }

        var monitor = MonitorFromWindow(handle, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        // Borderless and exclusive fullscreen both cover the whole monitor.
        return window.Left <= info.Monitor.Left && window.Top <= info.Monitor.Top &&
            window.Right >= info.Monitor.Right && window.Bottom >= info.Monitor.Bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    public HandheldRunningGame? Poll()
    {
        var steamGame = _steamMonitor?.Poll();
        if (steamGame is not null)
        {
            _lastNonSteamGame = null;
            return steamGame;
        }

        var foregroundTarget = _resolveForegroundTarget();
        // Apps the user added a profile for by hand count even when they don't look like games.
        if (foregroundTarget is not null &&
            (LooksLikeGame(foregroundTarget, _isFullscreen) || _hasProfile(KeyForExecutable(foregroundTarget.ExecutablePath))))
        {
            var executablePath = NormalizeExecutablePath(foregroundTarget.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                _lastNonSteamGame = new HandheldRunningGame(
                    $"exe:{executablePath.ToLowerInvariant()}",
                    string.Empty,
                    ResolveTitle(foregroundTarget),
                    executablePath,
                    foregroundTarget.ProcessId);
                return _lastNonSteamGame;
            }
        }

        if (_lastNonSteamGame is not null && _isProcessAlive(_lastNonSteamGame.ProcessId))
        {
            return _lastNonSteamGame;
        }

        _lastNonSteamGame = null;
        return null;
    }

    private static string ResolveTitle(ForegroundTargetCandidate target) =>
        string.IsNullOrWhiteSpace(target.WindowTitle)
            ? Path.GetFileNameWithoutExtension(target.ExecutablePath)
            : target.WindowTitle.Trim();

    private static string NormalizeExecutablePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
