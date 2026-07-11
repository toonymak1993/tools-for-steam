using Microsoft.Win32;

namespace SteamLoader.App.Infrastructure.Handheld;

internal sealed class HandheldPerformanceProfileCoordinator
{
    private readonly HandheldPerformanceService _performanceService;
    private readonly SteamGameProcessMonitor? _gameMonitor;
    private readonly WindowsProfileNotificationService _notificationService;

    public HandheldPerformanceProfileCoordinator(
        HandheldPerformanceService performanceService,
        string? steamRootPath,
        WindowsProfileNotificationService notificationService)
    {
        _performanceService = performanceService;
        _notificationService = notificationService;
        _gameMonitor = string.IsNullOrWhiteSpace(steamRootPath)
            ? null
            : new SteamGameProcessMonitor(steamRootPath);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_gameMonitor is null ||
            !HandheldDeviceCatalog.IsSupported(HandheldDeviceCatalog.Detect()))
        {
            return;
        }

        var resumeRequested = 0;
        PowerModeChangedEventHandler powerModeChanged = (_, args) =>
        {
            if (args.Mode == PowerModes.Resume)
            {
                Interlocked.Exchange(ref resumeRequested, 1);
            }
        };

        try
        {
            SystemEvents.PowerModeChanged += powerModeChanged;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            var lastTickAt = DateTimeOffset.UtcNow;
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var now = DateTimeOffset.UtcNow;
                var resumed = Interlocked.Exchange(ref resumeRequested, 0) == 1 ||
                    now - lastTickAt > TimeSpan.FromSeconds(8);
                lastTickAt = now;

                var powerResult = _performanceService.RefreshPowerState(forceReapply: resumed);
                if (powerResult is not null)
                {
                    _notificationService.ShowProfileApplied(powerResult);
                }

                HandheldAutomaticProfileResult? result;
                var game = _gameMonitor.Poll();
                if (game is null)
                {
                    result = _performanceService.ClearCurrentGameAndRestoreGlobal();
                }
                else
                {
                    result = _performanceService.ApplyAutomaticProfile(game);
                }

                if (result is not null)
                {
                    _notificationService.ShowProfileApplied(result);
                }
            }
        }
        finally
        {
            SystemEvents.PowerModeChanged -= powerModeChanged;
        }
    }
}
