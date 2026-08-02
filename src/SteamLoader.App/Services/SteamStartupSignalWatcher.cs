using System.Management;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SteamLoader.App.Services;

internal sealed class SteamStartupSignalWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint WineventOutofcontext = 0x0000;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly WinEventDelegate _windowCallback;
    private readonly List<IntPtr> _windowHooks = [];
    private ManagementEventWatcher? _processStartWatcher;
    private ManagementEventWatcher? _processStopWatcher;
    private volatile bool _disposed;

    public SteamStartupSignalWatcher()
    {
        _windowCallback = OnWindowEvent;
        TryStartProcessWatchers();
        TryAddWindowHook(EventSystemForeground);
        TryAddWindowHook(EventObjectShow);
        TryAddWindowHook(EventObjectHide);
    }

    public async Task<bool> WaitForSignalAsync(TimeSpan fallbackInterval, CancellationToken cancellationToken)
    {
        return await _signal.WaitAsync(fallbackInterval, cancellationToken).ConfigureAwait(false);
    }

    private void TryStartProcessWatchers()
    {
        const string processFilter =
            "ProcessName = 'steam.exe' OR " +
            "ProcessName = 'steamwebhelper.exe' OR " +
            "ProcessName = 'GameOverlayUI.exe' OR " +
            "ProcessName = 'steamerrorreporter.exe'";

        try
        {
            _processStartWatcher = new ManagementEventWatcher(new WqlEventQuery(
                $"SELECT * FROM Win32_ProcessStartTrace WHERE {processFilter}"));
            _processStopWatcher = new ManagementEventWatcher(new WqlEventQuery(
                $"SELECT * FROM Win32_ProcessStopTrace WHERE {processFilter}"));
            _processStartWatcher.EventArrived += OnProcessEvent;
            _processStopWatcher.EventArrived += OnProcessEvent;
            _processStartWatcher.Start();
            _processStopWatcher.Start();
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write($"Steam process event watcher unavailable; using fallback observations: {exception.Message}");
            DisposeProcessWatchers();
        }
    }

    private void TryAddWindowHook(uint eventId)
    {
        try
        {
            var hook = SetWinEventHook(
                eventId,
                eventId,
                IntPtr.Zero,
                _windowCallback,
                0,
                0,
                WineventOutofcontext);
            if (hook != IntPtr.Zero)
            {
                _windowHooks.Add(hook);
            }
        }
        catch
        {
        }
    }

    private void OnProcessEvent(object sender, EventArrivedEventArgs eventArgs) => Signal();

    private void OnWindowEvent(
        IntPtr hook,
        uint eventType,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        // Ignore accessibility events for child controls. Only top-level window
        // show/hide and foreground changes can alter splash readiness.
        if (objectId == 0 && windowHandle != IntPtr.Zero && IsSteamWindow(windowHandle))
        {
            Signal();
        }
    }

    private static bool IsSteamWindow(IntPtr windowHandle)
    {
        _ = GetWindowThreadProcessId(windowHandle, out var rawProcessId);
        if (rawProcessId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)rawProcessId);
            return process.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void Signal()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
            // A native callback can already be in flight while its hook is
            // removed. That benign shutdown race must never escape unmanaged code.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeProcessWatchers();
        foreach (var hook in _windowHooks)
        {
            UnhookWinEvent(hook);
        }

        _windowHooks.Clear();
        _signal.Dispose();
    }

    private void DisposeProcessWatchers()
    {
        foreach (var watcher in new[] { _processStartWatcher, _processStopWatcher })
        {
            if (watcher is null)
            {
                continue;
            }

            try
            {
                watcher.Stop();
            }
            catch
            {
            }

            watcher.Dispose();
        }

        _processStartWatcher = null;
        _processStopWatcher = null;
    }

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookModule,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr eventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}
