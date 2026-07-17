using System.Diagnostics;
using SteamLoader.App.Infrastructure.Performance;

namespace SteamLoader.App.Infrastructure.Handheld;

internal sealed class HybridGameProcessMonitor
{
    private readonly SteamGameProcessMonitor? _steamMonitor;
    private readonly Func<ForegroundTargetCandidate?> _resolveForegroundTarget;
    private readonly Func<int, bool> _isProcessAlive;
    private HandheldRunningGame? _lastNonSteamGame;

    public HybridGameProcessMonitor(string? steamRootPath)
        : this(
            string.IsNullOrWhiteSpace(steamRootPath) ? null : new SteamGameProcessMonitor(steamRootPath),
            PerformanceForegroundTargetResolver.TryResolve,
            IsProcessAlive)
    {
    }

    internal HybridGameProcessMonitor(
        SteamGameProcessMonitor? steamMonitor,
        Func<ForegroundTargetCandidate?> resolveForegroundTarget,
        Func<int, bool> isProcessAlive)
    {
        _steamMonitor = steamMonitor;
        _resolveForegroundTarget = resolveForegroundTarget;
        _isProcessAlive = isProcessAlive;
    }

    public HandheldRunningGame? Poll()
    {
        var steamGame = _steamMonitor?.Poll();
        if (steamGame is not null)
        {
            _lastNonSteamGame = null;
            return steamGame;
        }

        var foregroundTarget = _resolveForegroundTarget();
        if (foregroundTarget is not null)
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
