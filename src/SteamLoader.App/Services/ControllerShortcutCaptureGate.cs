namespace SteamLoader.App.Services;

/// <summary>
/// Tells the controller shortcut pollers that the Overlay settings recorder is
/// listening. While a user records a combination such as View + LB, the
/// current shortcuts must not open the Steam menu or Quick Access underneath
/// the recorder. The recorder polls <c>/api/settings/overlay/input-state</c>
/// every ~65 ms; each poll keeps the gate open for a short window, so the gate
/// closes by itself when the recorder stops or the popup disappears.
/// The elevated gamepad helper runs its own shortcut poller in another
/// process, so the state is mirrored into a named event in the session.
/// </summary>
public static class ControllerShortcutCaptureGate
{
    internal const string EventName = @"Local\ToolsForSteam.OverlayShortcutCapture";
    internal static readonly TimeSpan HoldOpenWindow = TimeSpan.FromMilliseconds(750);

    private static readonly object Sync = new();
    private static long _activeUntilTicks;
    private static EventWaitHandle? _ownedSignal;
    private static EventWaitHandle? _observedSignal;
    private static DateTime _nextObserveAttemptUtc;
    private static System.Threading.Timer? _resetTimer;

    /// <summary>Marks the recorder as listening for the next <see cref="HoldOpenWindow"/>.</summary>
    public static void MarkActive()
    {
        var activeUntilUtc = DateTime.UtcNow + HoldOpenWindow;
        Interlocked.Exchange(ref _activeUntilTicks, activeUntilUtc.Ticks);
        lock (Sync)
        {
            try
            {
                _ownedSignal ??= new EventWaitHandle(false, EventResetMode.ManualReset, EventName);
                _ownedSignal.Set();
            }
            catch
            {
                // The in-process window below still protects the main poller.
            }

            _resetTimer ??= new System.Threading.Timer(_ => ResetWhenExpired(), null, Timeout.Infinite, Timeout.Infinite);
            _resetTimer.Change(HoldOpenWindow + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>True while the recorder (in this or the main TFS process) is listening.</summary>
    public static bool IsActive()
    {
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _activeUntilTicks))
        {
            return true;
        }

        lock (Sync)
        {
            if (_ownedSignal is not null)
            {
                // This process owns the signal and resets it on expiry.
                return false;
            }

            try
            {
                if (_observedSignal is null)
                {
                    var nowUtc = DateTime.UtcNow;
                    if (nowUtc < _nextObserveAttemptUtc)
                    {
                        return false;
                    }

                    _nextObserveAttemptUtc = nowUtc + TimeSpan.FromSeconds(1);
                    if (!EventWaitHandle.TryOpenExisting(EventName, out var opened))
                    {
                        return false;
                    }

                    _observedSignal = opened;
                }

                return _observedSignal.WaitOne(0);
            }
            catch
            {
                _observedSignal?.Dispose();
                _observedSignal = null;
                return false;
            }
        }
    }

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _activeUntilTicks, 0);
        lock (Sync)
        {
            try
            {
                _ownedSignal?.Reset();
            }
            catch
            {
            }
        }
    }

    private static void ResetWhenExpired()
    {
        lock (Sync)
        {
            var remaining = new DateTime(Interlocked.Read(ref _activeUntilTicks), DateTimeKind.Utc) - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                _resetTimer?.Change(remaining + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
                return;
            }

            try
            {
                _ownedSignal?.Reset();
            }
            catch
            {
            }
        }
    }
}
