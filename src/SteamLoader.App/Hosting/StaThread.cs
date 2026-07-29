using System.Collections.Concurrent;

namespace SteamLoader.App.Hosting;

internal static class StaThread
{
    private static readonly BlockingCollection<Action> WorkQueue = new();
    private static readonly Thread Worker = StartWorker();

    public static Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration cancellationRegistration = default;

        if (cancellationToken.CanBeCanceled)
        {
            cancellationRegistration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
        }

        WorkQueue.Add(() =>
        {
            try
            {
                if (completion.Task.IsCompleted)
                {
                    return;
                }

                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                cancellationRegistration.Dispose();
            }
        });

        return completion.Task;
    }

    private static Thread StartWorker()
    {
        var thread = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "ToolsForSteam.STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    private static void ProcessQueue()
    {
        foreach (var workItem in WorkQueue.GetConsumingEnumerable())
        {
            workItem();
        }
    }
}
