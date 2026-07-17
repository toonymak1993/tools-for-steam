using System.Text.Json;

namespace SteamLoader.App.Infrastructure.Handheld;

internal sealed class HandheldHardwareCommandProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _commandPath;
    private readonly string _statusPath;
    private readonly string _dataDirectory;
    private readonly Action<string> _log;
    private long _lastNonce;

    public HandheldHardwareCommandProcessor(string dataDirectory, Action<string> log)
    {
        _dataDirectory = dataDirectory;
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

            if (command.Operation is "oem-software-enable" or "oem-software-disable")
            {
                var enabled = string.Equals(command.Operation, "oem-software-enable", StringComparison.Ordinal);
                var message = HandheldSystemControlService.ApplyMsiClawOemSoftwareState(_dataDirectory, enabled);
                var previous = LoadStatus();
                SaveStatus(previous with
                {
                    Nonce = command.Nonce,
                    Success = true,
                    Message = message,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Operation = command.Operation
                });
                _log($"oem-software-state enabled={enabled} message={message}");
                return;
            }

            if (string.Equals(command.Operation, "set-lighting", StringComparison.Ordinal))
            {
                var device = HandheldDeviceCatalog.Detect();
                var adapter = HandheldLightingControllerFactory.Create(device);
                var target = command.Lighting ?? throw new InvalidOperationException("The RGB command has no lighting payload.");
                var hardware = adapter.Apply(target);
                var previous = LoadStatus();
                SaveStatus(new HandheldHardwareStatus(
                    command.Nonce, true, HandheldPerformanceService.IsPawnIoInstalled(), previous.AppliedTdpWatts, hardware,
                    $"RGB {target.Effect} applied at {target.Brightness}% brightness.", DateTimeOffset.UtcNow,
                    command.Operation, true, $"RGB {target.Effect} applied at {target.Brightness}% brightness.",
                    previous.AppliedSpptWatts, previous.AppliedFpptWatts));
                _log($"lighting-applied effect={target.Effect} brightness={target.Brightness} hardware={hardware}");
                return;
            }

            if (!string.Equals(command.Operation, "set-tdp", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The hardware operation is not supported by this device adapter.");
            }

            var sppt = command.SpptWatts > 0 ? command.SpptWatts : command.TdpWatts;
            var fppt = command.FpptWatts > 0 ? command.FpptWatts : sppt;
            var cpu = new MsiClawA8TdpController().Apply(command.TdpWatts, sppt, fppt);
            var previousStatus = LoadStatus();
            SaveStatus(new HandheldHardwareStatus(
                command.Nonce,
                true,
                true,
                command.TdpWatts,
                cpu,
                $"SPL {command.TdpWatts} W, SPPT {sppt} W and FPPT {fppt} W applied.",
                DateTimeOffset.UtcNow,
                command.Operation,
                previousStatus.LightingApplied,
                previousStatus.LightingMessage,
                sppt,
                fppt));
            _log($"tdp-applied spl={command.TdpWatts} sppt={sppt} fppt={fppt} cpu={cpu}");
        }
        catch (Exception exception)
        {
            var previousStatus = LoadStatus();
            SaveStatus(new HandheldHardwareStatus(
                command.Nonce,
                false,
                HandheldPerformanceService.IsPawnIoInstalled(),
                string.Equals(command.Operation, "set-lighting", StringComparison.Ordinal) ? previousStatus.AppliedTdpWatts : 0,
                string.Empty,
                exception.Message,
                DateTimeOffset.UtcNow,
                command.Operation,
                string.Equals(command.Operation, "set-lighting", StringComparison.Ordinal) ? false : previousStatus.LightingApplied,
                string.Equals(command.Operation, "set-lighting", StringComparison.Ordinal) ? exception.Message : previousStatus.LightingMessage));
            _log($"hardware-command-failed operation={command.Operation} watts={command.TdpWatts} type={exception.GetType().Name} message={exception.Message}");
        }
    }

    private void SaveStatus(HandheldHardwareStatus status) => HandheldPerformanceService.WriteJsonAtomically(_statusPath, status);

    private HandheldHardwareStatus LoadStatus()
    {
        try
        {
            return File.Exists(_statusPath)
                ? JsonSerializer.Deserialize<HandheldHardwareStatus>(File.ReadAllText(_statusPath), JsonOptions) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }
}
