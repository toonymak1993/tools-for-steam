using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace SteamLoader.App.Infrastructure.SystemTools;

public sealed class BluetoothDeviceService : IDisposable
{
    private static readonly TimeSpan ScanDuration = TimeSpan.FromSeconds(12);
    private static readonly string[] RequestedProperties =
    [
        "System.Devices.Aep.DeviceAddress",
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.SignalStrength"
    ];

    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, BluetoothEntry> _devices = new(StringComparer.Ordinal);
    private readonly List<DeviceWatcher> _watchers = [];
    private CancellationTokenSource? _scanCancellation;
    private bool _available;
    private bool _powered;
    private bool _radioStateKnown;
    private bool _scanning;
    private string _pairingDeviceId = string.Empty;
    private string _statusText = "Open Bluetooth to load paired devices.";
    private DateTimeOffset? _scanEndsAt;
    private bool _disposed;

    public BluetoothDeviceSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return CreateSnapshotLocked();
        }
    }

    public async Task<BluetoothDeviceSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var radioTask = ReadRadioStateAsync();
            var classicTask = FindDevicesAsync(
                BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                "Bluetooth");
            var lowEnergyTask = FindDevicesAsync(
                BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                "Bluetooth LE");
            await Task.WhenAll(radioTask, classicTask, lowEnergyTask).WaitAsync(cancellationToken);

            var radioState = await radioTask;
            var pairedDevices = (await classicTask)
                .Concat(await lowEnergyTask)
                .ToArray();

            lock (_gate)
            {
                _available = radioState.Available;
                _powered = radioState.Powered;
                _radioStateKnown = radioState.Known;

                foreach (var staleId in _devices
                             .Where(pair => pair.Value.IsPaired)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    _devices.Remove(staleId);
                }

                foreach (var entry in pairedDevices)
                {
                    _devices[entry.Id] = entry;
                }

                _statusText = ResolveStatusTextLocked();
                return CreateSnapshotLocked();
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<BluetoothDeviceSnapshot> StartScanAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_scanning)
            {
                return CreateSnapshotLocked();
            }

            if (_radioStateKnown && (!_available || !_powered))
            {
                _statusText = _available
                    ? "Turn on Bluetooth in Windows before scanning."
                    : "No Bluetooth adapter was detected.";
                return CreateSnapshotLocked();
            }

            foreach (var staleId in _devices
                         .Where(pair => !pair.Value.IsPaired)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _devices.Remove(staleId);
            }

            _scanCancellation?.Cancel();
            _scanCancellation?.Dispose();
            _scanCancellation = new CancellationTokenSource();
            _scanEndsAt = DateTimeOffset.Now.Add(ScanDuration);

            try
            {
                AddWatcherLocked(
                    BluetoothDevice.GetDeviceSelectorFromPairingState(false),
                    "Bluetooth");
                AddWatcherLocked(
                    BluetoothLEDevice.GetDeviceSelectorFromPairingState(false),
                    "Bluetooth LE");
                _scanning = true;
                _statusText = "Scanning for nearby Bluetooth devices...";
                foreach (var watcher in _watchers)
                {
                    watcher.Start();
                }
            }
            catch
            {
                StopWatchersLocked();
                throw;
            }

            _ = StopScanAfterDelayAsync(_scanCancellation.Token);
            return CreateSnapshotLocked();
        }
    }

    public async Task<BluetoothDeviceSnapshot> PairAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        DeviceInformation device;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(deviceId) ||
                !_devices.TryGetValue(deviceId, out var entry))
            {
                throw new InvalidOperationException("This Bluetooth device is no longer available. Scan again.");
            }

            if (entry.IsPaired)
            {
                return CreateSnapshotLocked();
            }

            device = entry.Device;
            _pairingDeviceId = deviceId;
            _statusText = $"Pairing {entry.Name}...";
        }

        try
        {
            var result = await device.Pairing.PairAsync().AsTask(cancellationToken);
            if (result.Status is not DevicePairingResultStatus.Paired and
                not DevicePairingResultStatus.AlreadyPaired)
            {
                throw new InvalidOperationException(GetPairingError(result.Status));
            }

            await RefreshAsync(cancellationToken);
            lock (_gate)
            {
                _statusText = $"{device.Name} is paired.";
                return CreateSnapshotLocked();
            }
        }
        finally
        {
            lock (_gate)
            {
                _pairingDeviceId = string.Empty;
                if (_statusText.StartsWith("Pairing ", StringComparison.Ordinal))
                {
                    _statusText = ResolveStatusTextLocked();
                }
            }
        }
    }

    public BluetoothDeviceSnapshot OpenSettings()
    {
        using var process = Process.Start(CreateSettingsStartInfo())
            ?? throw new InvalidOperationException("Bluetooth settings could not be opened.");

        lock (_gate)
        {
            _statusText = "Bluetooth settings opened in Windows.";
            return CreateSnapshotLocked();
        }
    }

    internal static ProcessStartInfo CreateSettingsStartInfo()
        => new("ms-settings:bluetooth")
        {
            UseShellExecute = true
        };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _scanCancellation?.Cancel();
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            StopWatchersLocked();
        }

        _refreshGate.Dispose();
    }

    private static async Task<(bool Available, bool Powered, bool Known)> ReadRadioStateAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var bluetoothRadios = radios.Where(radio => radio.Kind == RadioKind.Bluetooth).ToArray();
            return (
                Available: bluetoothRadios.Length > 0,
                Powered: bluetoothRadios.Any(radio => radio.State == RadioState.On),
                Known: true);
        }
        catch
        {
            // Some desktop policies block radio-state reads while still allowing
            // AssociationEndpoint discovery and pairing. Keep discovery usable.
            return (Available: true, Powered: true, Known: false);
        }
    }

    private static async Task<IReadOnlyList<BluetoothEntry>> FindDevicesAsync(
        string selector,
        string transport)
    {
        var devices = await DeviceInformation.FindAllAsync(
            selector,
            RequestedProperties,
            DeviceInformationKind.AssociationEndpoint);
        return devices.Select(device => CreateEntry(device, transport)).ToArray();
    }

    private void AddWatcherLocked(string selector, string transport)
    {
        var watcher = DeviceInformation.CreateWatcher(
            selector,
            RequestedProperties,
            DeviceInformationKind.AssociationEndpoint);
        watcher.Added += (_, device) => HandleDeviceAdded(device, transport);
        watcher.Updated += (_, update) => HandleDeviceUpdated(update);
        watcher.Removed += (_, update) => HandleDeviceRemoved(update.Id);
        watcher.Stopped += (_, _) => HandleWatcherStopped();
        _watchers.Add(watcher);
    }

    private void HandleDeviceAdded(DeviceInformation device, string transport)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _devices[device.Id] = CreateEntry(device, transport);
            _statusText = ResolveStatusTextLocked();
        }
    }

    private void HandleDeviceUpdated(DeviceInformationUpdate update)
    {
        lock (_gate)
        {
            if (_disposed || !_devices.TryGetValue(update.Id, out var existing))
            {
                return;
            }

            existing.Device.Update(update);
            _devices[update.Id] = CreateEntry(existing.Device, existing.Transport);
            _statusText = ResolveStatusTextLocked();
        }
    }

    private void HandleDeviceRemoved(string deviceId)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _devices.Remove(deviceId);
            _statusText = ResolveStatusTextLocked();
        }
    }

    private void HandleWatcherStopped()
    {
        lock (_gate)
        {
            if (_watchers.All(watcher => watcher.Status is DeviceWatcherStatus.Stopped or DeviceWatcherStatus.Aborted))
            {
                _scanning = false;
                _scanEndsAt = null;
                _statusText = ResolveStatusTextLocked();
            }
        }
    }

    private async Task StopScanAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ScanDuration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            StopWatchersLocked();
            _scanning = false;
            _scanEndsAt = null;
            _statusText = ResolveStatusTextLocked();
        }
    }

    private void StopWatchersLocked()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                {
                    watcher.Stop();
                }
            }
            catch
            {
            }
        }

        _watchers.Clear();
        _scanning = false;
        _scanEndsAt = null;
    }

    private BluetoothDeviceSnapshot CreateSnapshotLocked()
    {
        var items = _devices.Values
            .GroupBy(
                entry => string.IsNullOrWhiteSpace(entry.Address)
                    ? entry.Id
                    : entry.Address,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(entry => entry.IsPaired)
                .ThenByDescending(entry => entry.IsConnected)
                .ThenBy(entry => entry.Transport, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(entry => entry.IsConnected)
            .ThenByDescending(entry => entry.IsPaired)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => new BluetoothDeviceItem(
                entry.Id,
                entry.Name,
                entry.Address,
                entry.Transport,
                entry.IsPaired,
                entry.CanPair,
                entry.IsConnected,
                entry.SignalStrength))
            .ToArray();

        return new BluetoothDeviceSnapshot(
            Available: _available,
            Powered: _powered,
            RadioStateKnown: _radioStateKnown,
            Scanning: _scanning,
            PairingDeviceId: _pairingDeviceId,
            ScanEndsAt: _scanEndsAt,
            Devices: items,
            StatusText: _statusText);
    }

    private string ResolveStatusTextLocked()
    {
        if (_radioStateKnown && !_available)
        {
            return "No Bluetooth adapter was detected.";
        }

        if (_radioStateKnown && !_powered)
        {
            return "Bluetooth is turned off in Windows.";
        }

        if (_scanning)
        {
            return "Scanning for nearby Bluetooth devices...";
        }

        var pairedCount = _devices.Values.Count(entry => entry.IsPaired);
        var availableCount = _devices.Values.Count(entry => !entry.IsPaired);
        return availableCount > 0
            ? $"{availableCount} nearby device{(availableCount == 1 ? string.Empty : "s")} found."
            : pairedCount > 0
                ? $"{pairedCount} paired device{(pairedCount == 1 ? string.Empty : "s")}."
                : "No Bluetooth devices are currently visible.";
    }

    private static BluetoothEntry CreateEntry(DeviceInformation device, string transport)
        => new(
            Id: device.Id,
            Name: string.IsNullOrWhiteSpace(device.Name) ? "Bluetooth device" : device.Name.Trim(),
            Address: ReadStringProperty(device, "System.Devices.Aep.DeviceAddress"),
            Transport: transport,
            IsPaired: device.Pairing.IsPaired,
            CanPair: device.Pairing.CanPair,
            IsConnected: ReadBooleanProperty(device, "System.Devices.Aep.IsConnected"),
            SignalStrength: ReadNullableIntProperty(device, "System.Devices.Aep.SignalStrength"),
            Device: device);

    private static string ReadStringProperty(DeviceInformation device, string key)
        => device.Properties.TryGetValue(key, out var value)
            ? Convert.ToString(value)?.Trim() ?? string.Empty
            : string.Empty;

    private static bool ReadBooleanProperty(DeviceInformation device, string key)
        => device.Properties.TryGetValue(key, out var value) &&
           value is not null &&
           Convert.ToBoolean(value);

    private static int? ReadNullableIntProperty(DeviceInformation device, string key)
        => device.Properties.TryGetValue(key, out var value) && value is not null
            ? Convert.ToInt32(value)
            : null;

    private static string GetPairingError(DevicePairingResultStatus status)
        => status switch
        {
            DevicePairingResultStatus.NotReadyToPair => "The device is not ready to pair.",
            DevicePairingResultStatus.AuthenticationFailure => "Bluetooth authentication failed.",
            DevicePairingResultStatus.AuthenticationTimeout => "Bluetooth authentication timed out.",
            DevicePairingResultStatus.ConnectionRejected => "The device rejected the connection.",
            DevicePairingResultStatus.TooManyConnections => "The device has too many active connections.",
            DevicePairingResultStatus.PairingCanceled => "Bluetooth pairing was cancelled.",
            DevicePairingResultStatus.AccessDenied => "Windows denied access to Bluetooth pairing.",
            _ => $"Bluetooth pairing failed ({status})."
        };

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record BluetoothEntry(
        string Id,
        string Name,
        string Address,
        string Transport,
        bool IsPaired,
        bool CanPair,
        bool IsConnected,
        int? SignalStrength,
        DeviceInformation Device);
}

public sealed record BluetoothDeviceSnapshot(
    bool Available,
    bool Powered,
    bool RadioStateKnown,
    bool Scanning,
    string PairingDeviceId,
    DateTimeOffset? ScanEndsAt,
    IReadOnlyList<BluetoothDeviceItem> Devices,
    string StatusText);

public sealed record BluetoothDeviceItem(
    string Id,
    string Name,
    string Address,
    string Transport,
    bool IsPaired,
    bool CanPair,
    bool IsConnected,
    int? SignalStrength);
