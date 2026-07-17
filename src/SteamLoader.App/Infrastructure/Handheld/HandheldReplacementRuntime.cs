using System.Management;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using Nefarius.Drivers.HidHide;

namespace SteamLoader.App.Infrastructure.Handheld;

internal sealed class HandheldReplacementRuntime : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _dataDirectory;
    private readonly string _statePath;
    private readonly Action<string> _log;
    private ViiperHandheldControllerBridge? _bridge;

    public HandheldReplacementRuntime(string dataDirectory, Action<string> log)
    {
        _dataDirectory = dataDirectory;
        _statePath = Path.Combine(dataDirectory, "handheld-replacement-state.json");
        _log = log;
    }

    public static int Prepare(string dataDirectory, bool usbIpOwnedByTfs, bool hidHideOwnedByTfs)
    {
        var device = HandheldDeviceCatalog.Detect();
        if (!HandheldDeviceCatalog.IsSupported(device))
        {
            return 0;
        }

        var path = Path.Combine(dataDirectory, "handheld-replacement-state.json");
        var current = Load(path);
        Save(path, current with
        {
            Requested = true,
            DeviceId = device.Id,
            ProductCode = device.ProductCode,
            UsbIpOwnedByTfs = current.UsbIpOwnedByTfs || usbIpOwnedByTfs,
            HidHideOwnedByTfs = current.HidHideOwnedByTfs || hidHideOwnedByTfs,
            Phase = "prepared",
            LastError = string.Empty,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        return 0;
    }

    public static int PrepareOemSoftware(string dataDirectory)
    {
        var device = HandheldDeviceCatalog.Detect();
        if (!HandheldDeviceCatalog.IsSupported(device))
        {
            return 0;
        }

        try
        {
            var status = HandheldSystemControlService.ApplyMsiClawOemSoftwareState(
                dataDirectory,
                enabled: false,
                replacementVerified: true);
            WriteSetupLog(dataDirectory, $"OEM replacement preparation succeeded: {status}");
            return 0;
        }
        catch (Exception exception)
        {
            WriteSetupLog(dataDirectory, $"OEM replacement preparation failed: {exception}");
            return 1;
        }
    }

    public static int RestoreForUninstall(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "handheld-replacement-state.json");
        var loaded = Load(path);
        if (!string.Equals(loaded.DeviceId, "msi-claw-a8", StringComparison.Ordinal))
        {
            return 0;
        }

        var state = loaded with { Requested = false, Phase = "restoring", UpdatedAt = DateTimeOffset.UtcNow };
        Save(path, state);
        StopBundledViiperProcesses();
        RemoveOwnedHidHideConfiguration(path, state);
        MsiClawControllerProtocol.TryRestoreXInputMode(out _);
        try { HandheldSystemControlService.ApplyMsiClawOemSoftwareState(dataDirectory, enabled: true); }
        catch { }
        Save(path, Load(path) with { Phase = "restored", UpdatedAt = DateTimeOffset.UtcNow });
        return 0;
    }

    public static int SuspendForUpdate(string dataDirectory)
    {
        var device = HandheldDeviceCatalog.Detect();
        if (!HandheldDeviceCatalog.IsSupported(device))
        {
            return 0;
        }

        var path = Path.Combine(dataDirectory, "handheld-replacement-state.json");
        var state = Load(path);
        if (!state.Requested ||
            !string.Equals(state.DeviceId, device.Id, StringComparison.Ordinal) ||
            !string.Equals(state.ProductCode, device.ProductCode, StringComparison.Ordinal))
        {
            return 0;
        }

        try
        {
            var installRoot = Directory.GetParent(Path.GetFullPath(dataDirectory))?.FullName;
            StopBundledViiperProcesses(installRoot);
            RemoveOwnedHidHideConfiguration(path, state);
            var controllerRestored = MsiClawControllerProtocol.TryRestoreXInputMode(out var controllerStatus);
            var oemStatus = HandheldSystemControlService.ApplyMsiClawOemSoftwareState(
                dataDirectory,
                enabled: false,
                replacementVerified: true);
            Save(path, Load(path) with
            {
                Requested = true,
                Phase = controllerRestored ? "update-suspended" : "update-suspend-failed",
                LastError = controllerRestored ? string.Empty : controllerStatus,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            WriteSetupLog(dataDirectory, $"Replacement suspended for update without restoring MSI Center M: {controllerStatus} {oemStatus}");
            return controllerRestored ? 0 : 1;
        }
        catch (Exception exception)
        {
            Save(path, Load(path) with
            {
                Requested = true,
                Phase = "update-suspend-failed",
                LastError = exception.Message,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            WriteSetupLog(dataDirectory, $"Replacement update suspension failed: {exception}");
            return 1;
        }
    }

    public static int RemoveOwnedDrivers(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "handheld-replacement-state.json");
        var state = Load(path);
        var failed = false;

        if (state.HidHideOwnedByTfs && CanRemoveOwnedHidHide())
        {
            if (TryRunRegisteredUninstaller("HidHide"))
            {
                state = state with { HidHideOwnedByTfs = false };
            }
            else
            {
                failed = true;
            }
        }

        if (state.UsbIpOwnedByTfs)
        {
            if (TryRunRegisteredUninstaller("USBip"))
            {
                state = state with { UsbIpOwnedByTfs = false };
            }
            else
            {
                failed = true;
            }
        }

        Save(path, state with
        {
            Phase = failed ? "driver-removal-incomplete" : "drivers-removed",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        return failed ? 1 : 0;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var state = Load(_statePath);
            if (!state.Requested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                RequireSupportedDevice(state);
                EnsureHidHideApplicationAccess(state);
                _bridge = new ViiperHandheldControllerBridge(_dataDirectory, _log);
                await _bridge.StartAsync(
                        () =>
                        {
                            var success = MsiClawControllerProtocol.TryEnableTfsButtonMode(out var status);
                            return (success, status);
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                state = AddDirectInputDevicesToHidHide(Load(_statePath));
                var oemStatus = HandheldSystemControlService.ApplyMsiClawOemSoftwareState(
                    _dataDirectory,
                    enabled: false,
                    replacementVerified: true);
                Save(_statePath, state with
                {
                    Phase = "active",
                    LastError = string.Empty,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                _log($"handheld-replacement active; {oemStatus}");

                var replacementStillRequested = true;
                while (_bridge.IsRunning && !cancellationToken.IsCancellationRequested)
                {
                    replacementStillRequested = Load(_statePath).Requested;
                    if (!replacementStillRequested)
                    {
                        break;
                    }
                    HandheldSystemControlService.EnforceMsiClawOemSoftwareUnavailable();
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }

                if (!replacementStillRequested)
                {
                    await StopBridgeAsync().ConfigureAwait(false);
                    var restoreState = Load(_statePath);
                    RemoveOwnedHidHideConfiguration(_statePath, restoreState);
                    MsiClawControllerProtocol.TryRestoreXInputMode(out var restoreStatus);
                    HandheldSystemControlService.ApplyMsiClawOemSoftwareState(_dataDirectory, enabled: true);
                    _log($"handheld-replacement stopped on request; {restoreStatus}");
                    continue;
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("The VIIPER controller bridge stopped unexpectedly.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log($"handheld-replacement failed: {exception.Message}");
                await StopBridgeAsync().ConfigureAwait(false);
                FailSafeRestore(exception.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
        }

        await StopBridgeAsync().ConfigureAwait(false);
        RemoveOwnedHidHideConfiguration(_statePath, Load(_statePath));
        MsiClawControllerProtocol.TryRestoreXInputMode(out var shutdownStatus);
        _log($"handheld-replacement stopped; {shutdownStatus}");
    }

    public async ValueTask DisposeAsync() => await StopBridgeAsync().ConfigureAwait(false);

    private void RequireSupportedDevice(HandheldReplacementState state)
    {
        var device = HandheldDeviceCatalog.Detect();
        if (!HandheldDeviceCatalog.IsSupported(device) ||
            !string.Equals(device.Id, state.DeviceId, StringComparison.Ordinal) ||
            !string.Equals(device.ProductCode, state.ProductCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The prepared supported handheld is no longer detected.");
        }

        var viiperPath = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "VIIPER", "viiper.exe");
        if (!File.Exists(viiperPath))
        {
            throw new FileNotFoundException("The VIIPER runtime is not installed.", viiperPath);
        }
    }

    private void EnsureHidHideApplicationAccess(HandheldReplacementState state)
    {
        var service = new HidHideControlService();
        if (!service.IsInstalled)
        {
            throw new InvalidOperationException("HidHide is required for the supported handheld replacement.");
        }

        var applicationPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The TFS helper executable path is unavailable.");
        var added = (state.AddedHidHideApplications ?? []).ToList();
        if (!service.ApplicationPaths.Contains(applicationPath, StringComparer.OrdinalIgnoreCase))
        {
            service.AddApplicationPath(applicationPath);
            added.Add(applicationPath);
        }

        Save(_statePath, state with
        {
            AddedHidHideApplications = added.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            OriginalHidHideActive = state.HidHideConfigured ? state.OriginalHidHideActive : service.IsActive,
            HidHideConfigured = true,
            Phase = "starting",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private HandheldReplacementState AddDirectInputDevicesToHidHide(HandheldReplacementState state)
    {
        var service = new HidHideControlService();
        var added = (state.AddedBlockedInstanceIds ?? []).ToList();
        foreach (var instanceId in FindMsiDirectInputInstanceIds())
        {
            if (!service.BlockedInstanceIds.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
            {
                service.AddBlockedInstanceId(instanceId);
                added.Add(instanceId);
            }
        }

        if (added.Count == 0)
        {
            throw new InvalidOperationException("No MSI DirectInput device instance was available for HidHide.");
        }

        service.IsActive = true;
        state = state with
        {
            AddedBlockedInstanceIds = added.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Save(_statePath, state);
        return state;
    }

    private void FailSafeRestore(string error)
    {
        var state = Load(_statePath);
        RemoveOwnedHidHideConfiguration(_statePath, state);
        MsiClawControllerProtocol.TryRestoreXInputMode(out var controllerStatus);
        try { HandheldSystemControlService.ApplyMsiClawOemSoftwareState(_dataDirectory, enabled: true); }
        catch { }
        Save(_statePath, Load(_statePath) with
        {
            Requested = false,
            Phase = "failed-safe",
            LastError = $"{error} {controllerStatus}".Trim(),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static void WriteSetupLog(string dataDirectory, string message)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.AppendAllText(
                Path.Combine(dataDirectory, "handheld-oem-setup.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private async Task StopBridgeAsync()
    {
        if (_bridge is null)
        {
            return;
        }

        await _bridge.DisposeAsync().ConfigureAwait(false);
        _bridge = null;
    }

    private static string[] FindMsiDirectInputInstanceIds()
    {
        var ids = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_PnPEntity");
            using var results = searcher.Get();
            foreach (ManagementObject device in results)
            {
                var id = Convert.ToString(device["DeviceID"]) ?? string.Empty;
                if (id.Contains("VID_0DB0&PID_1902", StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(id);
                }
            }
        }
        catch
        {
        }

        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void RemoveOwnedHidHideConfiguration(string path, HandheldReplacementState state)
    {
        try
        {
            var service = new HidHideControlService();
            if (!service.IsInstalled)
            {
                return;
            }

            foreach (var id in state.AddedBlockedInstanceIds ?? [])
            {
                if (service.BlockedInstanceIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    service.RemoveBlockedInstanceId(id);
                }
            }

            foreach (var application in state.AddedHidHideApplications ?? [])
            {
                if (service.ApplicationPaths.Contains(application, StringComparer.OrdinalIgnoreCase))
                {
                    service.RemoveApplicationPath(application);
                }
            }

            if (state.HidHideConfigured && !state.OriginalHidHideActive && service.BlockedInstanceIds.Count == 0)
            {
                service.IsActive = false;
            }
        }
        catch
        {
        }

        Save(path, state with
        {
            AddedBlockedInstanceIds = [],
            AddedHidHideApplications = [],
            HidHideConfigured = false,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static bool CanRemoveOwnedHidHide()
    {
        try
        {
            var service = new HidHideControlService();
            return !service.IsInstalled ||
                (service.ApplicationPaths.Count == 0 && service.BlockedInstanceIds.Count == 0);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRunRegisteredUninstaller(string displayNameFragment)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var product = uninstall.OpenSubKey(subKeyName);
                    var displayName = Convert.ToString(product?.GetValue("DisplayName")) ?? string.Empty;
                    if (!displayName.Contains(displayNameFragment, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var command = Convert.ToString(product?.GetValue("QuietUninstallString"));
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        command = Convert.ToString(product?.GetValue("UninstallString"));
                    }

                    if (!string.IsNullOrWhiteSpace(command) && RunUninstallCommand(command))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
        }

        // Absence already means the requested end state has been reached.
        return !IsDriverInstalled(displayNameFragment);
    }

    private static bool RunUninstallCommand(string command)
    {
        var trimmed = command.Trim();
        string executable;
        string arguments;
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote < 0)
            {
                return false;
            }

            executable = trimmed[1..closingQuote];
            arguments = trimmed[(closingQuote + 1)..].Trim();
        }
        else
        {
            var firstSpace = trimmed.IndexOf(' ');
            executable = firstSpace < 0 ? trimmed : trimmed[..firstSpace];
            arguments = firstSpace < 0 ? string.Empty : trimmed[(firstSpace + 1)..].Trim();
        }

        if (Path.GetFileName(executable).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) &&
            !arguments.Contains("/quiet", StringComparison.OrdinalIgnoreCase) &&
            !arguments.Contains("/qn", StringComparison.OrdinalIgnoreCase))
        {
            arguments += " /qn /norestart";
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null || !process.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds))
            {
                return false;
            }

            return process.ExitCode is 0 or 1605 or 3010;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDriverInstalled(string displayNameFragment)
    {
        if (displayNameFragment.Equals("HidHide", StringComparison.OrdinalIgnoreCase))
        {
            return Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\HidHide") is not null;
        }

        using var usbIpUde = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\usbip2_ude");
        using var usbIpStub = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\usbip2_stub");
        return usbIpUde is not null || usbIpStub is not null;
    }

    private static void StopBundledViiperProcesses(string? installRoot = null)
    {
        var expectedPath = Path.GetFullPath(
            Path.Combine(installRoot ?? AppContext.BaseDirectory, "ThirdParty", "VIIPER", "viiper.exe"));
        var executablePaths = ReadViiperExecutablePaths();
        foreach (var process in Process.GetProcessesByName("viiper"))
        {
            try
            {
                executablePaths.TryGetValue(process.Id, out var executablePath);
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    executablePath = process.MainModule?.FileName;
                }
                if (string.Equals(executablePath, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static IReadOnlyDictionary<int, string> ReadViiperExecutablePaths()
    {
        var result = new Dictionary<int, string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name='viiper.exe'");
            using var processes = searcher.Get();
            foreach (ManagementObject process in processes)
            {
                var processId = Convert.ToInt32(process["ProcessId"]);
                var executablePath = Convert.ToString(process["ExecutablePath"]);
                if (!string.IsNullOrWhiteSpace(executablePath))
                {
                    result[processId] = executablePath;
                }
            }
        }
        catch
        {
        }
        return result;
    }

    private static HandheldReplacementState Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<HandheldReplacementState>(File.ReadAllText(path), JsonOptions) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    private static void Save(string path, HandheldReplacementState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        HandheldPerformanceService.WriteJsonAtomically(path, state);
    }
}

internal sealed record HandheldReplacementState(
    bool Requested = false,
    string DeviceId = "",
    string ProductCode = "",
    bool UsbIpOwnedByTfs = false,
    bool HidHideOwnedByTfs = false,
    bool HidHideConfigured = false,
    bool OriginalHidHideActive = false,
    string[]? AddedHidHideApplications = null,
    string[]? AddedBlockedInstanceIds = null,
    string Phase = "not-prepared",
    string LastError = "",
    DateTimeOffset UpdatedAt = default);
