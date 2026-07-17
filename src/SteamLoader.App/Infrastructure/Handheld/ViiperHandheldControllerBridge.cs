using System.Diagnostics;
using System.Management;
using System.Security.Cryptography;
using System.Text.Json;
using Viiper.Client;
using Viiper.Client.Devices.Xbox360;
using Viiper.Client.Types;

namespace SteamLoader.App.Infrastructure.Handheld;

/// <summary>
/// Mirrors the MSI Claw DirectInput device to a VIIPER Xbox 360 device. This
/// class deliberately does not switch controller mode or touch OEM software;
/// the replacement lifecycle performs those operations transactionally only
/// after the VIIPER endpoint has been verified.
/// </summary>
internal sealed class ViiperHandheldControllerBridge : IAsyncDisposable
{
    internal const int UsbPort = 47661;
    internal const int ApiPort = 47662;
    internal const uint BusId = 4765;
    private static readonly TimeSpan VibrationSafetyTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly string _dataDirectory;
    private readonly string _hapticCommandPath;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _vibrationSync = new();
    private Process? _server;
    private FileSystemWatcher? _hapticWatcher;
    private ViiperClient? _client;
    private ViiperDevice? _device;
    private MsiClawDirectInputSource? _source;
    private string _deviceId = string.Empty;
    private Task? _pump;
    private CancellationTokenSource? _vibrationWatchdog;
    private CancellationTokenSource? _uiHapticStop;
    private long _vibrationGeneration;
    private long _lastUiHapticNonce;
    private readonly long _minimumUiHapticNonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private int _hapticPollTick;
    private int _hapticFallbackPollTick;
    private int _uiHapticPending;
    private int _cachedVibrationStrength = HandheldControllerSettingsStore.MsiClawA8DefaultVibrationStrengthPercent;
    private DateTimeOffset _vibrationSettingsReadAt;

    public ViiperHandheldControllerBridge(string dataDirectory, Action<string> log)
    {
        _dataDirectory = dataDirectory;
        _hapticCommandPath = Path.Combine(dataDirectory, "handheld-ui-haptic.json");
        _log = log;
    }

    public bool IsRunning => _pump is { IsCompleted: false } && _server is { HasExited: false };

    public async Task StartAsync(
        Func<(bool Success, string Status)> activateDirectInput,
        CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return;
        }

        var serverPath = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "VIIPER", "viiper.exe");
        if (!File.Exists(serverPath))
        {
            throw new FileNotFoundException("The bundled VIIPER server is missing.", serverPath);
        }

        Directory.CreateDirectory(_dataDirectory);
        // The producer uses an atomic temp-file rename. FileSystemWatcher may
        // match a rename against the temporary source name instead of the final
        // destination name, so watch the directory and filter the events here.
        _hapticWatcher = new FileSystemWatcher(_dataDirectory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _hapticWatcher.Created += OnUiHapticFileChanged;
        _hapticWatcher.Changed += OnUiHapticFileChanged;
        _hapticWatcher.Renamed += OnUiHapticFileChanged;
        _hapticWatcher.Error += (_, _) => Interlocked.Exchange(ref _uiHapticPending, 1);
        var profileRoot = Path.Combine(_dataDirectory, "viiper-profile");
        var profileConfig = Path.Combine(profileRoot, "VIIPER");
        Directory.CreateDirectory(profileConfig);
        var passwordPath = Path.Combine(profileConfig, "viiper.key.txt");
        var password = GetOrCreatePassword(passwordPath);

        var logPath = Path.Combine(_dataDirectory, "viiper-runtime.log");
        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            WorkingDirectory = Path.GetDirectoryName(serverPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("server");
        startInfo.ArgumentList.Add($"--usb.addr=127.0.0.1:{UsbPort}");
        startInfo.ArgumentList.Add($"--api.addr=127.0.0.1:{ApiPort}");
        startInfo.ArgumentList.Add("--api.require-local-host-auth=true");
        startInfo.ArgumentList.Add("--api.auto-attach-windows-native=true");
        startInfo.ArgumentList.Add($"--log.file={logPath}");
        startInfo.Environment["APPDATA"] = profileRoot;

        _server = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The bundled VIIPER server could not be started.");
        _server.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) _log($"viiper: {args.Data}");
        };
        _server.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) _log($"viiper-error: {args.Data}");
        };
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        try
        {
            _client = new ViiperClient("127.0.0.1", ApiPort, password);
            await WaitForServerAsync(_client, _server, cancellationToken).ConfigureAwait(false);

            try
            {
                await _client.BusCreateAsync(BusId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try { await _client.BusRemoveAsync(BusId, cancellationToken).ConfigureAwait(false); }
                catch { }
                await _client.BusCreateAsync(BusId, cancellationToken).ConfigureAwait(false);
            }

            var existingVirtualControllers = FindReadyVirtualControllerIds();

            var created = await _client.BusDeviceAddAsync(
                    BusId,
                    new DeviceCreateRequest
                    {
                        Type = "xbox360",
                        DeviceSpecific = new Dictionary<string, object?> { ["subType"] = 1 }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            _deviceId = created.DevID;
            _device = await _client.ConnectDeviceAsync(BusId, _deviceId, cancellationToken).ConfigureAwait(false);
            _device.OnDisconnect = () =>
            {
                StopVibration("VIIPER virtual controller disconnected");
                _log("VIIPER virtual controller disconnected.");
            };
            _device.OnOutput = ReadOutputAsync;

            await WaitForVirtualControllerAsync(existingVirtualControllers, cancellationToken).ConfigureAwait(false);
            var activation = activateDirectInput();
            _log(activation.Status);
            if (!activation.Success)
            {
                throw new InvalidOperationException(activation.Status);
            }

            var sourceTimeout = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            var sourceStatus = string.Empty;
            while (DateTimeOffset.UtcNow < sourceTimeout &&
                   (!MsiClawDirectInputSource.TryOpen(out _source, out sourceStatus) || _source is null))
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            if (_source is null)
            {
                throw new InvalidOperationException(sourceStatus);
            }

            _log(sourceStatus);
            _pump = Task.Run(() => PumpAsync(_shutdown.Token), CancellationToken.None);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(4));
        var consecutiveFailures = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_source is null || _device is null)
            {
                throw new InvalidOperationException("The VIIPER bridge lost its input or output endpoint.");
            }

            if (!_source.TryRead(out var physical, out var error))
            {
                consecutiveFailures++;
                if (consecutiveFailures >= 250)
                {
                    throw new InvalidOperationException($"MSI DirectInput remained unavailable: {error}");
                }

                continue;
            }

            consecutiveFailures = 0;
            await _device.SendAsync(ToViiperState(physical), cancellationToken).ConfigureAwait(false);
            if (++_hapticPollTick >= 4)
            {
                _hapticPollTick = 0;
                var watcherRequestedRead = Interlocked.Exchange(ref _uiHapticPending, 0) != 0;
                var fallbackRequestedRead = ++_hapticFallbackPollTick >= 6;
                if (watcherRequestedRead || fallbackRequestedRead)
                {
                    _hapticFallbackPollTick = 0;
                    ProcessLatestUiHaptic();
                }
            }
        }
    }

    internal static Xbox360Input ToViiperState(MsiClawPhysicalGamepadState state) => new()
    {
        Buttons = state.Buttons,
        Lt = state.LeftTrigger,
        Rt = state.RightTrigger,
        Lx = state.LeftX,
        Ly = state.LeftY,
        Rx = state.RightX,
        Ry = state.RightY,
    };

    private Task ReadOutputAsync(Stream stream) => ReadOutputCoreAsync(stream, _shutdown.Token);

    private async Task ReadOutputCoreAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            var output = new byte[Xbox360.OutputSize];
            await stream.ReadExactlyAsync(output, cancellationToken).ConfigureAwait(false);
            if (output.Length >= 2)
            {
                _ = ApplyVibration(output[0], output[1]);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StopVibration($"VIIPER vibration output failed: {exception.Message}");
        }
    }

    private VibrationApplication ApplyVibration(byte largeMotor, byte smallMotor)
    {
        lock (_vibrationSync)
        {
            RefreshVibrationStrength();
            var scaledLarge = MsiClawControllerProtocol.ScaleVibration(largeMotor, _cachedVibrationStrength);
            var scaledSmall = MsiClawControllerProtocol.ScaleVibration(smallMotor, _cachedVibrationStrength);
            var generation = ++_vibrationGeneration;

            _vibrationWatchdog?.Cancel();
            _vibrationWatchdog?.Dispose();
            _vibrationWatchdog = null;

            var applied = MsiClawControllerProtocol.TrySetVibration(scaledLarge, scaledSmall);
            if (scaledLarge == 0 && scaledSmall == 0)
            {
                return new(generation, applied, scaledLarge, scaledSmall);
            }

            var watchdog = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _vibrationWatchdog = watchdog;
            _ = StopVibrationAfterTimeoutAsync(generation, watchdog);
            return new(generation, applied, scaledLarge, scaledSmall);
        }
    }

    private void ProcessLatestUiHaptic()
    {
        if (!File.Exists(_hapticCommandPath))
        {
            return;
        }

        try
        {
            var command = JsonSerializer.Deserialize<HandheldUiHapticCommand>(
                File.ReadAllText(_hapticCommandPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (command is null || command.Nonce <= _lastUiHapticNonce || command.Nonce < _minimumUiHapticNonce)
            {
                return;
            }

            _lastUiHapticNonce = command.Nonce;
            if (!string.Equals(command.DeviceId, "msi-claw-a8", StringComparison.Ordinal) ||
                command.DurationMilliseconds is < 10 or > 150 ||
                command.LargeMotor > 220 || command.SmallMotor > 220)
            {
                _log("Rejected an invalid handheld UI haptic command.");
                return;
            }

            var application = ApplyVibration(command.LargeMotor, command.SmallMotor);
            _log(
                $"UI haptic received: large={application.ScaledLarge}, " +
                $"small={application.ScaledSmall}, duration={command.DurationMilliseconds} ms, " +
                $"HID write={(application.Applied ? "ok" : "failed")}.");
            _uiHapticStop?.Cancel();
            _uiHapticStop?.Dispose();
            var stop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _uiHapticStop = stop;
            _ = StopUiHapticAfterAsync(application.Generation, command.DurationMilliseconds, stop);
        }
        catch (IOException)
        {
            // The producer atomically replaces this file. A transient sharing
            // race is retried on the next 16 ms poll.
            Interlocked.Exchange(ref _uiHapticPending, 1);
        }
        catch (JsonException)
        {
            Interlocked.Exchange(ref _uiHapticPending, 1);
        }
    }

    private void OnUiHapticFileChanged(object sender, FileSystemEventArgs args)
    {
        if (IsUiHapticCommandFileEvent(args.Name) ||
            args is RenamedEventArgs renamed && IsUiHapticCommandFileEvent(renamed.OldName))
        {
            Interlocked.Exchange(ref _uiHapticPending, 1);
        }
    }

    internal static bool IsUiHapticCommandFileEvent(string? name)
    {
        const string commandFileName = "handheld-ui-haptic.json";
        var fileName = Path.GetFileName(name);
        return string.Equals(fileName, commandFileName, StringComparison.OrdinalIgnoreCase) ||
               fileName?.StartsWith(commandFileName + ".", StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task StopUiHapticAfterAsync(
        long generation,
        int durationMilliseconds,
        CancellationTokenSource stop)
    {
        try
        {
            await Task.Delay(durationMilliseconds, stop.Token).ConfigureAwait(false);
            lock (_vibrationSync)
            {
                if (generation != _vibrationGeneration || stop.IsCancellationRequested)
                {
                    return;
                }

                _vibrationGeneration++;
                _vibrationWatchdog?.Cancel();
                _vibrationWatchdog?.Dispose();
                _vibrationWatchdog = null;
                MsiClawControllerProtocol.TrySetVibration(0, 0);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_uiHapticStop, stop))
            {
                _uiHapticStop = null;
            }
            stop.Dispose();
        }
    }

    private async Task StopVibrationAfterTimeoutAsync(long generation, CancellationTokenSource watchdog)
    {
        try
        {
            await Task.Delay(VibrationSafetyTimeout, watchdog.Token).ConfigureAwait(false);
            lock (_vibrationSync)
            {
                if (generation != _vibrationGeneration || watchdog.IsCancellationRequested)
                {
                    return;
                }

                MsiClawControllerProtocol.TrySetVibration(0, 0);
                _vibrationGeneration++;
                if (ReferenceEquals(_vibrationWatchdog, watchdog))
                {
                    _vibrationWatchdog = null;
                }
                _log("MSI vibration safety timeout stopped stale rumble output.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _log($"MSI vibration safety stop failed: {exception.Message}");
        }
        finally
        {
            watchdog.Dispose();
        }
    }

    private void RefreshVibrationStrength()
    {
        if (DateTimeOffset.UtcNow - _vibrationSettingsReadAt < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        _cachedVibrationStrength = HandheldControllerSettingsStore.ReadVibrationStrengthPercent(
            _dataDirectory,
            "msi-claw-a8",
            HandheldControllerSettingsStore.MsiClawA8DefaultVibrationStrengthPercent);
        _vibrationSettingsReadAt = DateTimeOffset.UtcNow;
    }

    private void StopVibration(string? reason = null)
    {
        lock (_vibrationSync)
        {
            _vibrationGeneration++;
            _vibrationWatchdog?.Cancel();
            _vibrationWatchdog?.Dispose();
            _vibrationWatchdog = null;
            _uiHapticStop?.Cancel();
            _uiHapticStop?.Dispose();
            _uiHapticStop = null;
            MsiClawControllerProtocol.TrySetVibration(0, 0);
            if (!string.IsNullOrWhiteSpace(reason))
            {
                _log(reason);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
        }

        StopVibration();

        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { _log($"VIIPER pump stopped: {exception.Message}"); }
            _pump = null;
        }

        if (_hapticWatcher is not null)
        {
            _hapticWatcher.EnableRaisingEvents = false;
            _hapticWatcher.Dispose();
            _hapticWatcher = null;
        }

        _source?.Dispose();
        _source = null;

        if (_device is not null)
        {
            await _device.DisposeAsync().ConfigureAwait(false);
            _device = null;
        }

        if (_client is not null)
        {
            if (!string.IsNullOrEmpty(_deviceId))
            {
                try { await _client.BusDeviceRemoveAsync(BusId, _deviceId).ConfigureAwait(false); }
                catch { }
            }

            try { await _client.BusRemoveAsync(BusId).ConfigureAwait(false); }
            catch { }
            _client.Dispose();
            _client = null;
        }

        _deviceId = string.Empty;
        if (_server is not null)
        {
            try
            {
                if (!_server.HasExited)
                {
                    _server.Kill(entireProcessTree: true);
                    await _server.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch { }
            _server.Dispose();
            _server = null;
        }
    }

    private static async Task WaitForServerAsync(
        ViiperClient client,
        Process process,
        CancellationToken cancellationToken)
    {
        var timeout = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"VIIPER exited during startup with code {process.ExitCode}.");
            }

            try
            {
                await client.PingAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
            {
                lastError = exception;
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"VIIPER did not become ready: {lastError?.Message}");
    }

    private static async Task WaitForVirtualControllerAsync(
        IReadOnlySet<string> existingControllerIds,
        CancellationToken cancellationToken)
    {
        var timeout = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FindReadyVirtualControllerIds().Any(id => !existingControllerIds.Contains(id)))
            {
                return;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Windows did not enumerate the VIIPER Xbox 360 controller.");
    }

    private static HashSet<string> FindReadyVirtualControllerIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Status FROM Win32_PnPEntity " +
                "WHERE DeviceID LIKE '%VID_045E&PID_028E%'");
            using var results = searcher.Get();
            foreach (ManagementObject device in results)
            {
                var id = Convert.ToString(device["DeviceID"]);
                if (!string.IsNullOrWhiteSpace(id) &&
                    string.Equals(Convert.ToString(device["Status"]), "OK", StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(id);
                }
            }
        }
        catch
        {
        }
        return ids;
    }

    private static string GetOrCreatePassword(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (!string.IsNullOrEmpty(existing))
            {
                return existing;
            }
        }

        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(path, password);
        return password;
    }

    private readonly record struct VibrationApplication(
        long Generation,
        bool Applied,
        byte ScaledLarge,
        byte ScaledSmall);
}
