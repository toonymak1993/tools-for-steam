using System.Diagnostics;
using SteamLoader.App.Infrastructure.Processes;

namespace SteamLoader.App.Services;

/// <summary>
/// Shared Steam / Big Picture foreground activation used by handheld OEM buttons
/// and the external-game Quick Access fallback.
/// </summary>
public sealed class SteamWindowFocusService
{
    private readonly ProcessWindowService _windowService;

    public SteamWindowFocusService(ProcessWindowService windowService)
    {
        _windowService = windowService;
    }

    public async Task<string> FocusSteamWindowAsync(CancellationToken cancellationToken)
    {
        var candidates = GetSteamWindowCandidates()
            .OrderByDescending(window =>
                window.Title.Contains("Big Picture", StringComparison.OrdinalIgnoreCase) ||
                window.Title.Contains("Gamepad", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(window => window.Title.Contains("Steam", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(window =>
                string.Equals(window.ProcessName, "steamwebhelper", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var candidate in candidates)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    _windowService.ActivateWindow(candidate.Handle);
                    await Task.Delay(150, cancellationToken);
                    if (!_windowService.IsForegroundWindow(candidate.Handle))
                    {
                        continue;
                    }

                    // Confirm that Steam keeps focus after the original button
                    // event and any shell reactions have finished.
                    await Task.Delay(200, cancellationToken);
                    if (_windowService.IsForegroundWindow(candidate.Handle))
                    {
                        return $"steam-window-focus:{candidate.ProcessName}:{candidate.Title}";
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                }
            }
        }

        throw new InvalidOperationException("No open Steam or Big Picture window could be focused.");
    }

    public bool TryRestoreWindow(string handle)
    {
        try
        {
            _windowService.ActivateWindow(handle);
            return _windowService.IsForegroundWindow(handle);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<SteamWindowCandidate> GetSteamWindowCandidates()
    {
        var candidates = new List<SteamWindowCandidate>();
        foreach (var processName in new[] { "steamwebhelper", "steam" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    var handle = process.MainWindowHandle;
                    var title = process.MainWindowTitle;
                    if (handle != 0 && !string.IsNullOrWhiteSpace(title))
                    {
                        candidates.Add(new(
                            $"0x{handle.ToInt64():X}",
                            title.Trim(),
                            process.ProcessName));
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return candidates
            .DistinctBy(candidate => candidate.Handle, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed record SteamWindowCandidate(string Handle, string Title, string ProcessName);
}
