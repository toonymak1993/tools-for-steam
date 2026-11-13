using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Services;

namespace SteamLoader.App.Hosting;

/// <summary>
/// Tells the user inside Big Picture when Windows' own Xbox Mode is on while
/// TFS starts as the console. Both take over the screen at sign-in, so they
/// cannot be used together. Shown once per conflict and again whenever Xbox
/// Mode is turned on anew; TFS never changes the Xbox Mode setting itself.
/// </summary>
internal sealed class XboxModeConflictNotifier
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly SteamLoaderSettingsService _settingsService;
    private readonly QuickAccessLiveUpdateHub _liveUpdateHub;

    public XboxModeConflictNotifier(
        SteamLoaderSettingsService settingsService,
        QuickAccessLiveUpdateHub liveUpdateHub)
    {
        _settingsService = settingsService;
        _liveUpdateHub = liveUpdateHub;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var tracker = new XboxModeConflictTracker();
        var loggedConflict = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var conflict = _settingsService.GetSnapshot().WindowsXboxModeConflict;
                if (conflict && !loggedConflict)
                {
                    SteamStartupDiagnostics.Write(
                        "Windows Xbox Mode is on while TFS starts as the console; the user is asked to turn one of them off");
                }

                loggedConflict = conflict;
                if (tracker.ShouldNotify(conflict, _liveUpdateHub.HasSubscribersFor("notifications.show")))
                {
                    _liveUpdateHub.Publish(
                        "notifications.show",
                        new
                        {
                            title = "Windows Xbox full screen experience is on",
                            message = XboxModeConflictTracker.Message,
                            level = "error",
                            durationMs = 15000
                        });
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed check must never stop the background host.
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }
}

/// <summary>
/// Notify once per conflict, as soon as a Big Picture overlay can show it.
/// </summary>
internal sealed class XboxModeConflictTracker
{
    public const string Message =
        "The Windows Xbox full screen experience and the Tools for Steam console start both take over the screen at sign-in. " +
        "They cannot work together: turn it off in Windows Settings > Gaming > Full screen experience, " +
        "or switch Tools for Steam to eTray in Settings > General.";

    private bool _notified;

    public bool ShouldNotify(bool conflict, bool canShow)
    {
        if (!conflict)
        {
            _notified = false;
            return false;
        }

        if (_notified || !canShow)
        {
            return false;
        }

        _notified = true;
        return true;
    }
}
