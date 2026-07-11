namespace SteamLoader.App.Infrastructure.Helpers;

internal sealed class GamepadHelperSupervisor
{
    private static readonly TimeSpan HealthyCheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RecoveryCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupConfirmationDelay = TimeSpan.FromMilliseconds(750);

    private readonly Func<bool> _isRegistered;
    private readonly Func<bool> _isRunning;
    private readonly Func<GamepadHelperStartResult> _tryRun;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string> _log;
    private int _helperRunning;

    public GamepadHelperSupervisor(GamepadHelperScheduledTaskService taskService, string logPath)
        : this(
            taskService.IsRegistered,
            taskService.IsRunning,
            () => taskService.TryRun(out var errorText)
                ? new GamepadHelperStartResult(true, string.Empty)
                : new GamepadHelperStartResult(false, errorText),
            (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
            message => AppendLog(logPath, message))
    {
    }

    internal GamepadHelperSupervisor(
        Func<bool> isRegistered,
        Func<bool> isRunning,
        Func<GamepadHelperStartResult> tryRun,
        Func<TimeSpan, CancellationToken, Task> delay,
        Action<string> log)
    {
        _isRegistered = isRegistered;
        _isRunning = isRunning;
        _tryRun = tryRun;
        _delay = delay;
        _log = log;
    }

    public bool IsHelperRunning => Volatile.Read(ref _helperRunning) == 1;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        bool? previousRunning = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var running = await EnsureRunningOnceAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _helperRunning, running ? 1 : 0);

            if (previousRunning != running)
            {
                _log(running
                    ? "helper-state running"
                    : "helper-state unavailable; local HID fallback remains active");
                previousRunning = running;
            }

            await _delay(
                    running ? HealthyCheckInterval : RecoveryCheckInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal async Task<bool> EnsureRunningOnceAsync(CancellationToken cancellationToken)
    {
        if (!_isRegistered())
        {
            return false;
        }

        if (_isRunning())
        {
            return true;
        }

        var startResult = _tryRun();
        if (!startResult.Started)
        {
            _log($"helper-restart failed: {startResult.ErrorText}".TrimEnd());
            return false;
        }

        _log("helper-restart requested");
        await _delay(StartupConfirmationDelay, cancellationToken).ConfigureAwait(false);
        var running = _isRunning();
        if (!running)
        {
            _log("helper-restart was accepted but is not running yet; retry scheduled");
        }

        return running;
    }

    private static void AppendLog(string path, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(
                path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

internal sealed record GamepadHelperStartResult(bool Started, string ErrorText);
