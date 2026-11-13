using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SteamLoader.App.Services;

/// <summary>
/// Console Mode start: Explorer boots next to TFS, and its taskbar is a topmost
/// window created after the splash, so it would draw over the splash. This
/// keeps the taskbar and desktop icons hidden until the splash hands off.
/// It also watches the splash UI thread: if that thread stops responding, it
/// uncovers the Windows desktop instead of leaving a frozen splash on screen.
/// </summary>
public sealed class ConsoleStartChromeCover
{
    private const int ShowWindowHide = 0;
    private static readonly TimeSpan HideInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan UiHangLimit = TimeSpan.FromSeconds(20);
    // The splash coordinator ends every start long before this; it is only a
    // last line of defense against a taskbar that stays hidden forever.
    private static readonly TimeSpan MaximumCoverDuration = TimeSpan.FromMinutes(20);

    private readonly WindowsShellVisibilityService _visibilityService;
    private readonly ConsoleShellState _state = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private readonly List<IntPtr> _coverWindows = [];
    private Dispatcher? _uiDispatcher;
    private long _lastUiBeatTicks;
    private int _uiBeatPending;
    private bool _stopped;

    public ConsoleStartChromeCover(WindowsShellVisibilityService visibilityService)
    {
        _visibilityService = visibilityService;
    }

    public void Start()
    {
        _lastUiBeatTicks = Environment.TickCount64;
        // Tells the background host's taskbar guard to leave the taskbar
        // hidden until this splash hands off (or this process dies).
        using (var currentProcess = System.Diagnostics.Process.GetCurrentProcess())
        {
            _state.SetStartCoverOwner(currentProcess);
        }

        _ = Task.Run(RunAsync);
    }

    /// <summary>
    /// Registers a TFS window that covers the screen during the start, so the
    /// hang fallback can uncover the desktop. Call on the UI thread.
    /// </summary>
    public void AttachCoverWindow(Dispatcher uiDispatcher, IntPtr windowHandle)
    {
        lock (_gate)
        {
            _uiDispatcher ??= uiDispatcher;
            if (windowHandle != IntPtr.Zero)
            {
                _coverWindows.Add(windowHandle);
            }

            Interlocked.Exchange(ref _lastUiBeatTicks, Environment.TickCount64);
        }
    }

    /// <summary>
    /// Ends the cover. With <paramref name="restoreChrome"/> false the taskbar
    /// stays hidden for the background host, which keeps it hidden while Big
    /// Picture is up and restores it otherwise.
    /// </summary>
    public void Stop(bool restoreChrome)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
        }

        _cancellation.Cancel();
        _state.ClearStartCoverOwner();
        if (restoreChrome)
        {
            _visibilityService.RestoreShellChrome();
        }
    }

    private async Task RunAsync()
    {
        var startedAt = Environment.TickCount64;
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                Dispatcher? dispatcher;
                lock (_gate)
                {
                    if (_stopped)
                    {
                        return;
                    }

                    // Inside the lock: Stop() can never be overtaken by a hide.
                    _visibilityService.HideShellChrome();
                    dispatcher = _uiDispatcher;
                }

                if (dispatcher is not null)
                {
                    if (Environment.TickCount64 - Interlocked.Read(ref _lastUiBeatTicks) > UiHangLimit.TotalMilliseconds)
                    {
                        UncoverDesktopAfterUiHang();
                        return;
                    }

                    if (Interlocked.Exchange(ref _uiBeatPending, 1) == 0)
                    {
                        dispatcher.BeginInvoke(
                            DispatcherPriority.Normal,
                            () =>
                            {
                                Interlocked.Exchange(ref _lastUiBeatTicks, Environment.TickCount64);
                                Interlocked.Exchange(ref _uiBeatPending, 0);
                            });
                    }
                }

                if (Environment.TickCount64 - startedAt > MaximumCoverDuration.TotalMilliseconds)
                {
                    SteamStartupDiagnostics.Write("console start cover reached its safety limit; showing the taskbar");
                    Stop(restoreChrome: true);
                    return;
                }

                await Task.Delay(HideInterval, _cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write($"console start cover failed; showing the taskbar: {exception.Message}");
            Stop(restoreChrome: true);
        }
    }

    private void UncoverDesktopAfterUiHang()
    {
        IntPtr[] coverWindows;
        lock (_gate)
        {
            coverWindows = [.. _coverWindows];
        }

        SteamStartupDiagnostics.Write(
            $"console start UI stopped responding for {UiHangLimit.TotalSeconds:F0}s; uncovering the Windows desktop");
        foreach (var handle in coverWindows)
        {
            // Async on purpose: the owning thread is the one that hangs.
            ShowWindowAsync(handle, ShowWindowHide);
        }

        Stop(restoreChrome: true);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);
}
