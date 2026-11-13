namespace SteamLoader.App.Infrastructure.Curtain;

/// <summary>
/// Pure launch logic of TFS Curtain (no Win32 calls, unit tested): which
/// windows belong to the launching game, which ones are launchers to hide,
/// and when the game surface is ready so the curtain may hand over.
/// </summary>
internal sealed class CurtainLaunchTracker
{
    internal static readonly TimeSpan FullscreenStableFor = TimeSpan.FromMilliseconds(800);
    internal static readonly TimeSpan LargeSurfaceStableFor = TimeSpan.FromMilliseconds(2500);
    internal static readonly TimeSpan LargeSurfaceMinimumProcessAge = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan TitleMatchStableFor = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan TitleMatchMinimumElapsed = TimeSpan.FromSeconds(6);

    internal static readonly TimeSpan InteractiveLauncherStableFor = TimeSpan.FromMilliseconds(2500);

    /// <summary>
    /// Store clients that start the game on their own; their windows are
    /// hidden in Modern mode and restored afterwards.
    /// </summary>
    internal static readonly HashSet<string> LauncherProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "EADesktop", "EALauncher", "Origin", "OriginWebHelperService",
        "UbisoftConnect", "upc", "UplayWebCore", "UbisoftGameLauncher", "UbisoftGameLauncher64",
        "Battle.net", "EpicGamesLauncher", "EpicWebHelper", "GalaxyClient", "GalaxyClient Helper",
        "LauncherPatcher", "RockstarService", "SocialClubHelper", "PlayGTAV",
        "RiotClientServices", "RiotClientUx", "AmazonGames", "XboxPcApp", "XboxApp"
    };

    /// <summary>Processes that never are the game itself.</summary>
    internal static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "steamservice", "steamerrorreporter", "gameoverlayui", "gameoverlayui64",
        "ToolsForSteam", "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
        "TextInputHost", "LockApp", "SystemSettings", "dwm", "ctfmon", "conhost", "OpenConsole", "cmd",
        "powershell", "pwsh", "WindowsTerminal", "WerFault", "WerFaultSecure", "CrashReporter",
        "UnityCrashHandler32", "UnityCrashHandler64", "CrashReportClient", "BsSndRpt", "BsSndRpt64",
        "DXSETUP", "vcredist_x64", "vcredist_x86", "VC_redist.x64", "VC_redist.x86", "dotNetFx40_Full_setup",
        "msiexec", "GameBar", "GameBarFTServer", "XboxGameBarWidgets", "RTSS", "MSIAfterburner",
        "Discord", "Overwolf", "nvcontainer", "NVIDIA Share", "NVIDIA Overlay", "AMDRSServ", "RadeonSoftware"
    };

    private readonly Dictionary<IntPtr, SurfaceState> _surfaces = [];

    public CurtainLaunchTracker(string expectedTitle)
    {
        ExpectedTitle = expectedTitle ?? string.Empty;
    }

    public string ExpectedTitle { get; }

    public static CurtainWindowRole Classify(
        CurtainWindowInfo window,
        CurtainProcessInfo? process,
        IReadOnlySet<uint> baselineProcessIds,
        IReadOnlySet<IntPtr> baselineWindows,
        uint ownProcessId)
    {
        if (process is null || window.ProcessId == ownProcessId || IgnoredProcesses.Contains(process.Name))
        {
            return CurtainWindowRole.Ignore;
        }

        if (LauncherProcesses.Contains(process.Name))
        {
            return CurtainWindowRole.Launcher;
        }

        if (window.IsToolWindow)
        {
            return CurtainWindowRole.Ignore;
        }

        // Store (UWP / Game Pass) games draw into an ApplicationFrameHost window
        // that already ran before the launch; only its new windows count.
        if (string.Equals(process.Name, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            return baselineWindows.Contains(window.Handle) ? CurtainWindowRole.Ignore : CurtainWindowRole.Game;
        }

        // A window that was already on screen when Play was pressed is never
        // the game, even if its process is young.
        return baselineProcessIds.Contains(window.ProcessId) || baselineWindows.Contains(window.Handle)
            ? CurtainWindowRole.Ignore
            : CurtainWindowRole.Game;
    }

    /// <summary>
    /// Feeds one monitor tick of game windows. Returns the ready surface, or
    /// null while the game is still building its window.
    /// </summary>
    public CurtainReadySurface? Observe(
        IReadOnlyList<CurtainGameSurface> gameSurfaces,
        DateTimeOffset now,
        TimeSpan elapsedSinceLaunch)
    {
        var seen = new HashSet<IntPtr>();
        CurtainReadySurface? ready = null;
        AttentionSurface = null;
        foreach (var surface in gameSurfaces)
        {
            seen.Add(surface.Window.Handle);
            if (!_surfaces.TryGetValue(surface.Window.Handle, out var state))
            {
                state = new SurfaceState(now, surface.Window.Bounds);
                _surfaces[surface.Window.Handle] = state;
            }
            else if (state.Bounds != surface.Window.Bounds)
            {
                state.Bounds = surface.Window.Bounds;
                state.LastGeometryChange = now;
            }

            var stableFor = now - state.LastGeometryChange;
            if (AttentionSurface is null &&
                surface.Window.IsForeground &&
                stableFor >= InteractiveLauncherStableFor &&
                LooksLikeInteractiveLauncher(surface))
            {
                // A game's own launcher waits for a click (Paradox, 2K, ...);
                // the curtain must step aside instead of hiding it.
                AttentionSurface = new CurtainReadySurface(surface.Window.Handle, surface.Window.ProcessId, "interactive-launcher");
                continue;
            }

            var reason = Evaluate(surface, stableFor, now, elapsedSinceLaunch);
            if (reason is not null && ready is null)
            {
                ready = new CurtainReadySurface(surface.Window.Handle, surface.Window.ProcessId, reason);
            }
        }

        foreach (var stale in _surfaces.Keys.Where(handle => !seen.Contains(handle)).ToArray())
        {
            _surfaces.Remove(stale);
        }

        return ready;
    }

    /// <summary>Set by <see cref="Observe"/> when a game launcher waits for the user.</summary>
    public CurtainReadySurface? AttentionSurface { get; private set; }

    internal static bool LooksLikeInteractiveLauncher(CurtainGameSurface surface) =>
        surface.ProcessName.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
        surface.Window.Title.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
        surface.Window.Title.Contains("launch options", StringComparison.OrdinalIgnoreCase);

    private string? Evaluate(
        CurtainGameSurface surface,
        TimeSpan stableFor,
        DateTimeOffset now,
        TimeSpan elapsedSinceLaunch)
    {
        var window = surface.Window;
        var monitor = window.Monitor;
        if (monitor.Width <= 0 || monitor.Height <= 0 || window.Bounds.Width < 320 || window.Bounds.Height < 200)
        {
            return null;
        }

        var widthRatio = window.Bounds.Width / (double)monitor.Width;
        var heightRatio = window.Bounds.Height / (double)monitor.Height;
        var areaRatio = window.Bounds.Area / (double)monitor.Area;

        if (widthRatio >= 0.9 && heightRatio >= 0.85 && stableFor >= FullscreenStableFor)
        {
            return "fullscreen";
        }

        var processAge = now - surface.ProcessStartedAt;
        if (widthRatio >= 0.55 && heightRatio >= 0.55 && areaRatio >= 0.30 &&
            stableFor >= LargeSurfaceStableFor &&
            processAge >= LargeSurfaceMinimumProcessAge)
        {
            return "large-window";
        }

        if (window.Bounds.Width >= 640 && window.Bounds.Height >= 360 &&
            stableFor >= TitleMatchStableFor &&
            elapsedSinceLaunch >= TitleMatchMinimumElapsed &&
            TitlesLikelyMatch(ExpectedTitle, window.Title))
        {
            return "windowed-title-match";
        }

        return null;
    }

    internal static bool TitlesLikelyMatch(string expectedTitle, string windowTitle)
    {
        var ignoredWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "edition", "enhanced", "game", "launcher", "steam", "the", "windows", "remastered", "definitive"
        };
        var expectedWords = (expectedTitle ?? string.Empty)
            .Split([' ', ':', '-', '_', '–', '—', '®', '™'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()))
            .Where(word => word.Length >= 4 && !ignoredWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (expectedWords.Length == 0 || string.IsNullOrWhiteSpace(windowTitle))
        {
            return false;
        }

        return expectedWords.Any(word => windowTitle.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class SurfaceState(DateTimeOffset firstSeen, CurtainRect bounds)
    {
        public CurtainRect Bounds { get; set; } = bounds;
        public DateTimeOffset LastGeometryChange { get; set; } = firstSeen;
    }
}

internal enum CurtainWindowRole
{
    Ignore,
    Launcher,
    Game
}

internal sealed record CurtainGameSurface(CurtainWindowInfo Window, string ProcessName, DateTimeOffset ProcessStartedAt);

internal sealed record CurtainReadySurface(IntPtr Handle, uint ProcessId, string Reason);
