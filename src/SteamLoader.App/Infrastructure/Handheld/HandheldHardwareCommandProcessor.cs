using System.Text.Json;

namespace SteamLoader.App.Infrastructure.Handheld;

internal sealed class HandheldHardwareCommandProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _commandPath;
    private readonly string _statusPath;
    private readonly Action<string> _log;
    private long _lastNonce;

    public HandheldHardwareCommandProcessor(string dataDirectory, Action<string> log)
    {
        _commandPath = Path.Combine(dataDirectory, "handheld-hardware-command.json");
        _statusPath = Path.Combine(dataDirectory, "handheld-hardware-status.json");
        _log = log;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ProcessLatestCommand();
            }
            catch (Exception exception)
            {
                _log($"hardware-command-loop-error type={exception.GetType().Name} message={exception.Message}");
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    private void ProcessLatestCommand()
    {
        if (!File.Exists(_commandPath))
        {
            return;
        }

        var command = JsonSerializer.Deserialize<HandheldHardwareCommand>(File.ReadAllText(_commandPath), JsonOptions);
        if (command is null || command.Nonce <= _lastNonce)
        {
            return;
        }

        _lastNonce = command.Nonce;
        try
        {
            if (!string.Equals(command.DeviceId, "msi-claw-a8", StringComparison.Ordinal) ||
                !string.Equals(command.ProductCode, "MS-1T8K", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The hardware command was rejected by the device allowlist.");
            }

            if (!string.Equals(command.Operation, "set-tdp", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The hardware operation is not supported by this device adapter.");
            }

            var cpu = new MsiClawA8TdpController().Apply(command.TdpWatts);
            SaveStatus(new HandheldHardwareStatus(
                command.Nonce,
                true,
                true,
                command.TdpWatts,
                cpu,
                $"{command.TdpWatts} W applied to STAPM, Slow and Fast limits.",
                DateTimeOffset.UtcNow));
            _log($"tdp-applied watts={command.TdpWatts} cpu={cpu}");
        }
        catch (Exception exception)
        {
            SaveStatus(new HandheldHardwareStatus(
                command.Nonce,
                false,
                HandheldPerformanceService.IsPawnIoInstalled(),
                0,
                string.Empty,
                exception.Message,
                DateTimeOffset.UtcNow));
            _log($"hardware-command-failed operation={command.Operation} watts={command.TdpWatts} type={exception.GetType().Name} message={exception.Message}");
        }
    }

    private void SaveStatus(HandheldHardwareStatus status) => HandheldPerformanceService.WriteJsonAtomically(_statusPath, status);
}
