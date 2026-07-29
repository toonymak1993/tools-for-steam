using System.Runtime.InteropServices;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Keeps Windows from entering system sleep while an OmniLibrary transfer is
/// active. The display remains governed by the user's normal power settings.
/// SetThreadExecutionState is thread-scoped, so the status monitor owns one
/// dedicated thread for its complete lifetime.
/// </summary>
internal static class OmniLibraryDownloadSleepBlocker
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        Continuous = 0x80000000,
    }

    public static IDisposable AcquireForCurrentThread()
    {
        var applied = SetThreadExecutionState(
            ExecutionState.Continuous | ExecutionState.SystemRequired) != 0;
        return new CurrentThreadLease(applied);
    }

    public static Task RunStatusMonitorAsync(CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew(
            () => RunStatusMonitor(cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private static void RunStatusMonitor(CancellationToken cancellationToken)
    {
        var blockingSleep = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var active = UnifySteamDownloadStatusStore.GetAll().Values.Any(status =>
                    UnifySteamDownloadStatusStore.IsBusyOperation(status.Status));
                if (active != blockingSleep)
                {
                    var result = SetThreadExecutionState(
                        active
                            ? ExecutionState.Continuous | ExecutionState.SystemRequired
                            : ExecutionState.Continuous);
                    if (result != 0)
                    {
                        blockingSleep = active;
                    }
                }

                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            if (blockingSleep)
            {
                SetThreadExecutionState(ExecutionState.Continuous);
            }
        }
    }

    private sealed class CurrentThreadLease(bool applied) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (applied)
            {
                SetThreadExecutionState(ExecutionState.Continuous);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState executionState);
}
