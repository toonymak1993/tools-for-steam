using System.Runtime.InteropServices;
using System.Text.Json;
using System.Diagnostics;
using System.Management;
using Microsoft.Win32;
using SteamLoader.App.Services;

namespace SteamLoader.App.Infrastructure.Handheld;

internal sealed class HandheldSystemControlService
{
    private static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid ProcessorBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly IReadOnlyList<HandheldOemActionDefinition> OemActions =
    [
        new("none", "Unassigned", "Detect the button but do not run an action."),
        new("steam-menu", "Steam Menu", "Open the main Steam menu."),
        new("quick-access", "Quick Access", "Open the Steam Quick Access menu."),
        new("focus-steam", "Focus Steam", "Switch to the open Steam or Big Picture window."),
        new("escape", "Escape", "Send the Escape key."),
        new("alt-tab", "Alt + Tab", "Switch to the next Windows application."),
        new("xbox-game-bar", "Xbox Game Bar", "Open Xbox Game Bar with Win + G."),
        new("task-manager", "Task Manager", "Open Task Manager with Ctrl + Shift + Escape."),
        new("custom-shortcut", "Custom Shortcut", "Send the keyboard shortcut entered for this button."),
    ];
    private static readonly string[] MsiCenterTaskNames = ["MSI_Center_M_Server", "MSI_Center_M_Updater"];
    private const string MsiFoundationServiceName = "MSI Foundation Service";
    private static DateTimeOffset _lastOemEnforcementAt;
    private readonly string _dataDirectory;
    private readonly string _afmfStatePath;
    private readonly string _oemBindingsPath;
    private readonly Func<(bool Success, string Status)> _enableTfsButtonMode;
    private readonly object _oemSync = new();
    private OemCaptureState? _capture;
    private readonly Dictionary<string, DateTimeOffset> _lastActionAtByButton = new(StringComparer.OrdinalIgnoreCase);

    public event Action<HandheldOemButtonBinding>? OemButtonPressed;

    public HandheldSystemControlService(
        string dataDirectory,
        Func<(bool Success, string Status)>? enableTfsButtonMode = null)
    {
        _dataDirectory = dataDirectory;
        _afmfStatePath = Path.Combine(dataDirectory, "handheld-afmf.json");
        _oemBindingsPath = Path.Combine(dataDirectory, "handheld-oem-buttons.json");
        _enableTfsButtonMode = enableTfsButtonMode ?? (() =>
        {
            var success = MsiClawControllerProtocol.TryEnableTfsButtonMode(out var status);
            return (success, status);
        });
    }

    public HandheldCpuBoostSnapshot GetCpuBoost()
    {
        if (PowerGetActiveScheme(0, out var pointer) != 0 || pointer == 0)
        {
            return new(false, 0, 0, "The active Windows power plan could not be read.");
        }

        try
        {
            var scheme = Marshal.PtrToStructure<Guid>(pointer);
            var subgroup = ProcessorSubgroup;
            var setting = ProcessorBoostMode;
            var acResult = PowerReadACValueIndex(0, ref scheme, ref subgroup, ref setting, out var ac);
            var dcResult = PowerReadDCValueIndex(0, ref scheme, ref subgroup, ref setting, out var dc);
            return acResult == 0 && dcResult == 0
                ? new(true, NormalizeBoostMode(ac), NormalizeBoostMode(dc), "CPU boost follows the active Windows power plan.")
                : new(false, 0, 0, "CPU boost is not exposed by the active Windows power plan.");
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    public HandheldCpuBoostSnapshot SetCpuBoost(string powerSource, bool enabled)
    {
        if (PowerGetActiveScheme(0, out var pointer) != 0 || pointer == 0)
        {
            throw new InvalidOperationException("The active Windows power plan could not be read.");
        }

        try
        {
            var scheme = Marshal.PtrToStructure<Guid>(pointer);
            var subgroup = ProcessorSubgroup;
            var setting = ProcessorBoostMode;
            var value = enabled ? 1u : 0u;
            var result = string.Equals(powerSource, "battery", StringComparison.OrdinalIgnoreCase)
                ? PowerWriteDCValueIndex(0, ref scheme, ref subgroup, ref setting, value)
                : PowerWriteACValueIndex(0, ref scheme, ref subgroup, ref setting, value);
            if (result != 0 || PowerSetActiveScheme(0, ref scheme) != 0)
            {
                throw new InvalidOperationException("Windows could not update CPU boost. Administrator rights may be required.");
            }
        }
        finally
        {
            LocalFree(pointer);
        }

        return GetCpuBoost();
    }

    public HandheldAfmfSnapshot GetAfmf()
    {
        var supported = Directory.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AMD", "CNext", "CNext"));
        var state = ReadAfmfState();
        return new(supported, state.Enabled,
            supported
                ? "Uses the AMD Software AFMF hotkey. VSync should be disabled in the game."
                : "AMD Software: Adrenalin Edition was not detected.");
    }

    public HandheldAfmfSnapshot SetAfmf(bool enabled)
    {
        var current = GetAfmf();
        if (!current.Supported)
        {
            throw new InvalidOperationException("AMD Software: Adrenalin Edition was not detected.");
        }

        if (current.Enabled != enabled)
        {
            SendAfmfHotkey();
            Directory.CreateDirectory(Path.GetDirectoryName(_afmfStatePath)!);
            File.WriteAllText(_afmfStatePath, JsonSerializer.Serialize(new AfmfState(enabled), JsonOptions));
        }
        return GetAfmf();
    }

    public HandheldOemSoftwareSnapshot GetOemSoftware(HandheldDeviceProfile device)
    {
        var supported = HandheldDeviceCatalog.IsSupported(device) && device.OemSoftware.Supported;
        if (!supported)
        {
            return new HandheldOemSoftwareSnapshot(
                false,
                device.Id,
                device.OemSoftware.SoftwareName,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                device.Controller.MinimumVibrationStrengthPercent,
                device.Controller.MaximumVibrationStrengthPercent,
                0,
                false,
                "OEM control is not available for this device.",
                BuildBindings(device),
                OemActions,
                new HandheldOemCaptureSnapshot(
                    false,
                    string.Empty,
                    string.Empty,
                    "Select a button and start Live Detect."));
        }

        var service = ReadMsiFoundationService();
        var tasks = MsiCenterTaskNames.Select(ReadScheduledTask).ToArray();
        var startupEntries = FindOemStartupEntries();
        var running = IsAnyMsiCenterProcessRunning();
        var detected = service.Installed || tasks.Any(task => task.Installed) || startupEntries.Count > 0 || running;
        var autostartEnabled = service.StartEnabled || tasks.Any(task => task.Enabled) || startupEntries.Count > 0;
        var controlActive = supported && detected && !service.Running && !service.StartEnabled &&
            tasks.Where(task => task.Installed).All(task => !task.Enabled && !task.Running) && !running;
        var bindings = BuildBindings(device);
        var vibrationStrengthPercent = device.Controller.VibrationSupported
            ? HandheldControllerSettingsStore.ReadVibrationStrengthPercent(
                _dataDirectory,
                device.Id,
                device.Controller.DefaultVibrationStrengthPercent)
            : 0;
        var uiHapticsEnabled = device.Controller.VibrationSupported &&
            HandheldControllerSettingsStore.ReadUiHapticsEnabled(_dataDirectory, device.Id);
        HandheldOemCaptureSnapshot capture;
        lock (_oemSync)
        {
            if (_capture is { } active && DateTimeOffset.UtcNow - active.StartedAt > TimeSpan.FromSeconds(20))
            {
                _capture = active with { Active = false, StatusText = "No input was detected. Start Live Detect and try again." };
            }

            capture = _capture is null
                ? new(false, string.Empty, string.Empty, "Select a button and start Live Detect.")
                : new(_capture.Active, _capture.ButtonId, _capture.DetectedInput, _capture.StatusText);
        }

        var statusText = !supported
            ? "OEM control is not available for this device."
            : !detected
                ? $"{device.OemSoftware.SoftwareName} was not detected."
                : controlActive
                    ? $"{device.OemSoftware.SoftwareName} is stopped. TFS button mappings are active."
                    : $"{device.OemSoftware.SoftwareName} is active. Stop it before using TFS button mappings.";

        return new(
            supported,
            device.Id,
            device.OemSoftware.SoftwareName,
            detected,
            autostartEnabled,
            running,
            controlActive,
            service.Installed,
            service.Running,
            service.StartEnabled,
            tasks.Any(task => task.Installed),
            tasks.Any(task => task.Enabled),
            tasks.Any(task => task.Running),
            device.Controller.VibrationSupported,
            device.Controller.MinimumVibrationStrengthPercent,
            device.Controller.MaximumVibrationStrengthPercent,
            vibrationStrengthPercent,
            uiHapticsEnabled,
            statusText,
            bindings,
            OemActions,
            capture);
    }

    public HandheldOemSoftwareSnapshot SetUiHapticsEnabled(HandheldDeviceProfile device, bool enabled)
    {
        if (!HandheldDeviceCatalog.IsSupported(device) || !device.Controller.VibrationSupported)
        {
            throw new InvalidOperationException("UI haptics are not supported by this device.");
        }

        HandheldControllerSettingsStore.WriteUiHapticsEnabled(
            _dataDirectory,
            device.Id,
            enabled,
            device.Controller.DefaultVibrationStrengthPercent);
        return GetOemSoftware(device);
    }

    public HandheldOemSoftwareSnapshot SetVibrationStrength(
        HandheldDeviceProfile device,
        int strengthPercent)
    {
        if (!HandheldDeviceCatalog.IsSupported(device) || !device.Controller.VibrationSupported)
        {
            throw new InvalidOperationException("Vibration control is not supported by this device.");
        }

        if (strengthPercent < device.Controller.MinimumVibrationStrengthPercent ||
            strengthPercent > device.Controller.MaximumVibrationStrengthPercent)
        {
            throw new ArgumentOutOfRangeException(
                nameof(strengthPercent),
                $"Vibration strength must be between {device.Controller.MinimumVibrationStrengthPercent} and {device.Controller.MaximumVibrationStrengthPercent} percent.");
        }

        HandheldControllerSettingsStore.WriteVibrationStrengthPercent(
            _dataDirectory,
            device.Id,
            strengthPercent);
        return GetOemSoftware(device);
    }

    public HandheldOemSoftwareSnapshot StartButtonCapture(HandheldDeviceProfile device, string buttonId)
    {
        RequireOemButton(device, buttonId);
        var modeStatus = string.Empty;
        if (GetOemSoftware(device).ControlActive)
        {
            (_, modeStatus) = _enableTfsButtonMode();
        }
        lock (_oemSync)
        {
            _capture = new OemCaptureState(
                true,
                buttonId,
                DateTimeOffset.UtcNow,
                string.Empty,
                string.IsNullOrWhiteSpace(modeStatus)
                    ? "Listening for the next MSI button press..."
                    : $"{modeStatus} Press the selected button now.");
        }

        return GetOemSoftware(device);
    }

    public HandheldOemSoftwareSnapshot CancelButtonCapture(HandheldDeviceProfile device)
    {
        lock (_oemSync)
        {
            if (_capture is not null)
            {
                _capture = _capture with { Active = false, StatusText = "Live Detect cancelled." };
            }
        }

        return GetOemSoftware(device);
    }

    public HandheldOemSoftwareSnapshot SetButtonBinding(
        HandheldDeviceProfile device,
        string buttonId,
        string actionId,
        string customShortcut)
    {
        RequireOemButton(device, buttonId);
        var action = OemActions.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, actionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentOutOfRangeException(nameof(actionId), "The selected OEM button action is not supported.");
        var shortcut = string.Equals(action.Id, "custom-shortcut", StringComparison.Ordinal)
            ? NormalizeShortcut(customShortcut)
            : (customShortcut ?? string.Empty).Trim();
        if (string.Equals(action.Id, "custom-shortcut", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(shortcut))
        {
            throw new ArgumentException("Enter a keyboard shortcut before selecting Custom Shortcut.");
        }

        lock (_oemSync)
        {
            var settings = LoadOemButtonSettings();
            var current = settings.Bindings?.FirstOrDefault(binding =>
                string.Equals(binding.ButtonId, buttonId, StringComparison.OrdinalIgnoreCase));
            var next = new StoredOemButtonBinding(
                buttonId,
                current?.InputCode ?? string.Empty,
                current?.InputName ?? string.Empty,
                action.Id,
                shortcut);
            var bindings = (settings.Bindings ?? [])
                .Where(binding => !string.Equals(binding.ButtonId, buttonId, StringComparison.OrdinalIgnoreCase))
                .Append(next)
                .ToArray();
            SaveOemButtonSettings(new(bindings));
        }

        return GetOemSoftware(device);
    }

    public void ObserveOemInput(HandheldDeviceProfile device, HidMenuButtonReport report)
    {
        if (!HandheldDeviceCatalog.IsSupported(device) || !device.OemSoftware.Supported ||
            !report.IsPressed || string.IsNullOrWhiteSpace(report.InputCode) || !IsMsiClawInputDevice(report.DeviceName))
        {
            return;
        }

        var sourceCode = $"{BuildMsiDeviceKey(report.DeviceName)}|{report.InputCode}";
        HandheldOemButtonBinding? actionBinding = null;
        lock (_oemSync)
        {
            if (_capture is { Active: true } capture)
            {
                var definition = RequireOemButton(device, capture.ButtonId);
                var settings = LoadOemButtonSettings();
                var current = settings.Bindings?.FirstOrDefault(binding =>
                    string.Equals(binding.ButtonId, capture.ButtonId, StringComparison.OrdinalIgnoreCase));
                var next = new StoredOemButtonBinding(
                    capture.ButtonId,
                    sourceCode,
                    report.Detail,
                    current?.ActionId ?? "none",
                    current?.CustomShortcut ?? string.Empty);
                SaveOemButtonSettings(new((settings.Bindings ?? [])
                    .Where(binding => !string.Equals(binding.ButtonId, capture.ButtonId, StringComparison.OrdinalIgnoreCase))
                    .Append(next)
                    .ToArray()));
                _capture = capture with
                {
                    Active = false,
                    DetectedInput = sourceCode,
                    StatusText = $"Detected {definition.Title}: {report.Detail}"
                };
                return;
            }

            var binding = BuildBindings(device).FirstOrDefault(candidate =>
                string.Equals(candidate.InputCode, sourceCode, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate.ActionId, "none", StringComparison.OrdinalIgnoreCase));
            if (binding is null)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (_lastActionAtByButton.TryGetValue(binding.ButtonId, out var previous) && now - previous < TimeSpan.FromMilliseconds(450))
            {
                return;
            }

            _lastActionAtByButton[binding.ButtonId] = now;
            actionBinding = binding;
        }

        if (actionBinding is not null && GetOemSoftware(device).ControlActive)
        {
            OemButtonPressed?.Invoke(actionBinding);
        }
    }

    internal static string ApplyMsiClawOemSoftwareState(
        string dataDirectory,
        bool enabled,
        bool replacementVerified = false)
    {
        var restorePath = Path.Combine(dataDirectory, "handheld-oem-msi-restore.json");
        if (enabled)
        {
            MsiClawControllerProtocol.TryRestoreXInputMode(out var controllerStatus);
            var restore = LoadMsiRestoreState(restorePath);
            var startService = restore?.ServiceStartEnabled ?? true;
            SetMsiServiceStartMode(startService ? "Automatic" : "Disabled");
            if (startService)
            {
                InvokeMsiService("StartService");
            }
            foreach (var taskName in MsiCenterTaskNames)
            {
                var taskEnabled = restore?.Tasks?.FirstOrDefault(task =>
                    string.Equals(task.TaskName, taskName, StringComparison.OrdinalIgnoreCase))?.Enabled ?? true;
                SetScheduledTaskState(
                    taskName,
                    enabled: taskEnabled,
                    run: taskEnabled && string.Equals(taskName, "MSI_Center_M_Server", StringComparison.Ordinal));
            }
            RestoreOemStartupEntries(restore?.StartupEntries ?? []);
            RestoreOemShortcuts(restore?.QuarantinedShortcuts ?? []);
            try { File.Delete(restorePath); }
            catch { }
            return $"MSI Center M service and autostarts were restored. {controllerStatus}";
        }

        if (HandheldDeviceCatalog.IsSupported(HandheldDeviceCatalog.Detect()) && !replacementVerified)
        {
            return "MSI Center M is managed by the mandatory TFS controller replacement. " +
                "It is disabled only after VIIPER and HidHide have been verified.";
        }

        if (!File.Exists(restorePath))
        {
            var service = ReadMsiFoundationService();
            var restore = new MsiOemRestoreState(
                service.StartEnabled,
                MsiCenterTaskNames.Select(taskName =>
                {
                    var task = ReadScheduledTask(taskName);
                    return new MsiTaskRestoreState(taskName, task.Enabled);
                }).ToArray(),
                FindOemStartupEntries(),
                FindOemShortcuts(dataDirectory));
            Directory.CreateDirectory(Path.GetDirectoryName(restorePath)!);
            File.WriteAllText(restorePath, JsonSerializer.Serialize(restore, JsonOptions));
        }

        foreach (var taskName in MsiCenterTaskNames)
        {
            var task = ReadScheduledTask(taskName);
            if (task.Installed && (task.Enabled || task.Running))
            {
                SetScheduledTaskState(taskName, enabled: false, run: false);
            }
        }
        var foundationService = ReadMsiFoundationService();
        if (foundationService.Installed && foundationService.Running)
        {
            InvokeMsiService("StopService");
        }
        if (foundationService.Installed && foundationService.StartEnabled)
        {
            SetMsiServiceStartMode("Disabled");
        }
        KillMsiCenterProcesses();
        RemoveOemStartupEntries();
        QuarantineOemShortcuts(LoadMsiRestoreState(restorePath)?.QuarantinedShortcuts ?? []);
        return "MSI Center M service, scheduled autostarts, and processes were stopped. " +
            "The verified controller bridge owns DirectInput mode.";
    }

    internal static void EnforceMsiClawOemSoftwareUnavailable()
    {
        KillMsiCenterProcesses();
        if (DateTimeOffset.UtcNow - _lastOemEnforcementAt < TimeSpan.FromSeconds(30))
        {
            return;
        }
        _lastOemEnforcementAt = DateTimeOffset.UtcNow;
        foreach (var taskName in MsiCenterTaskNames)
        {
            var task = ReadScheduledTask(taskName);
            if (task.Installed && (task.Enabled || task.Running))
            {
                SetScheduledTaskState(taskName, enabled: false, run: false);
            }
        }
        var foundationService = ReadMsiFoundationService();
        if (foundationService.Installed && foundationService.Running)
        {
            InvokeMsiService("StopService");
        }
        if (foundationService.Installed && foundationService.StartEnabled)
        {
            SetMsiServiceStartMode("Disabled");
        }
        RemoveOemStartupEntries();
    }

    private static List<StartupEntry> FindOemStartupEntries()
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        var entries = new List<StartupEntry>();
        using var key = Registry.CurrentUser.OpenSubKey(runKey);
        if (key is null) return entries;
        foreach (var name in key.GetValueNames())
        {
            var command = Convert.ToString(key.GetValue(name)) ?? string.Empty;
            if (IsMsiCenterMText(name) || IsMsiCenterMText(command)) entries.Add(new(runKey, name, command));
        }
        return entries;
    }

    private IReadOnlyList<HandheldOemButtonBinding> BuildBindings(HandheldDeviceProfile device)
    {
        var stored = LoadOemButtonSettings().Bindings ?? [];
        return device.OemSoftware.Buttons.Select(definition =>
        {
            var binding = stored.FirstOrDefault(candidate =>
                string.Equals(candidate.ButtonId, definition.Id, StringComparison.OrdinalIgnoreCase));
            var action = OemActions.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, binding?.ActionId, StringComparison.OrdinalIgnoreCase)) ?? OemActions[0];
            return new HandheldOemButtonBinding(
                definition.Id,
                definition.Title,
                definition.Description,
                binding?.InputCode ?? string.Empty,
                binding?.InputName ?? string.Empty,
                action.Id,
                action.Title,
                binding?.CustomShortcut ?? string.Empty,
                !string.IsNullOrWhiteSpace(binding?.InputCode));
        }).ToArray();
    }

    private static HandheldOemButtonDefinition RequireOemButton(HandheldDeviceProfile device, string buttonId)
    {
        if (!HandheldDeviceCatalog.IsSupported(device) || !device.OemSoftware.Supported)
        {
            throw new InvalidOperationException("OEM software control is not supported by this device.");
        }

        return device.OemSoftware.Buttons.FirstOrDefault(button =>
            string.Equals(button.Id, buttonId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentOutOfRangeException(nameof(buttonId), "The selected OEM button is not available on this device.");
    }

    private OemButtonSettings LoadOemButtonSettings()
    {
        try
        {
            return File.Exists(_oemBindingsPath)
                ? JsonSerializer.Deserialize<OemButtonSettings>(File.ReadAllText(_oemBindingsPath), JsonOptions) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    private void SaveOemButtonSettings(OemButtonSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_oemBindingsPath)!);
        var temporaryPath = $"{_oemBindingsPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, _oemBindingsPath, overwrite: true);
    }

    private static string NormalizeShortcut(string? value)
    {
        var normalized = string.Join("+", (value ?? string.Empty)
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.ToUpperInvariant()));
        if (normalized.Length > 80)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Keyboard shortcuts may not exceed 80 characters.");
        }

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            _ = ParseShortcut(normalized);
        }

        return normalized;
    }

    public static void SendOemKeyboardShortcut(string shortcut)
    {
        var keys = ParseShortcut(NormalizeShortcut(shortcut));
        var inputs = keys.Select(key => KeyInput(key, false))
            .Concat(keys.Reverse().Select(key => KeyInput(key, true)))
            .ToArray();
        if (inputs.Length == 0 || SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != (uint)inputs.Length)
        {
            throw new InvalidOperationException("The configured OEM keyboard shortcut could not be sent.");
        }
    }

    private static ushort[] ParseShortcut(string shortcut)
    {
        var tokens = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || tokens.Length > 5)
        {
            throw new ArgumentException("Use a shortcut such as Ctrl+Shift+F12.", nameof(shortcut));
        }

        return tokens.Select(token => token.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => (ushort)0x11,
            "SHIFT" => (ushort)0x10,
            "ALT" => (ushort)0x12,
            "WIN" or "WINDOWS" => (ushort)0x5B,
            "ESC" or "ESCAPE" => (ushort)0x1B,
            "TAB" => (ushort)0x09,
            "ENTER" or "RETURN" => (ushort)0x0D,
            "SPACE" => (ushort)0x20,
            "BACKSPACE" => (ushort)0x08,
            "DELETE" or "DEL" => (ushort)0x2E,
            "HOME" => (ushort)0x24,
            "END" => (ushort)0x23,
            "PAGEUP" or "PGUP" => (ushort)0x21,
            "PAGEDOWN" or "PGDN" => (ushort)0x22,
            "UP" => (ushort)0x26,
            "DOWN" => (ushort)0x28,
            "LEFT" => (ushort)0x25,
            "RIGHT" => (ushort)0x27,
            "F1" => (ushort)0x70,
            "F2" => (ushort)0x71,
            "F3" => (ushort)0x72,
            "F4" => (ushort)0x73,
            "F5" => (ushort)0x74,
            "F6" => (ushort)0x75,
            "F7" => (ushort)0x76,
            "F8" => (ushort)0x77,
            "F9" => (ushort)0x78,
            "F10" => (ushort)0x79,
            "F11" => (ushort)0x7A,
            "F12" => (ushort)0x7B,
            _ when token.Length == 1 && char.IsLetterOrDigit(token[0]) => (ushort)char.ToUpperInvariant(token[0]),
            _ => throw new ArgumentException($"Unsupported shortcut key: {token}", nameof(shortcut))
        }).Distinct().ToArray();
    }

    private static bool IsMsiClawInputDevice(string deviceName)
    {
        if (deviceName.StartsWith("MSI_ACPI:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalized = deviceName.Replace('#', '&');
        return normalized.Contains("VID_0DB0", StringComparison.OrdinalIgnoreCase) &&
            (normalized.Contains("PID_1901", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("PID_1902", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("PID_1903", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildMsiDeviceKey(string deviceName)
    {
        if (deviceName.StartsWith("MSI_ACPI:", StringComparison.OrdinalIgnoreCase))
        {
            return "msi-claw-a8:acpi";
        }

        foreach (var marker in new[] { "MI_00", "MI_01", "MI_02", "IG_03" })
        {
            if (deviceName.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return $"msi-claw-a8:{marker.ToLowerInvariant()}";
            }
        }

        return "msi-claw-a8:hid";
    }

    private static OemServiceState ReadMsiFoundationService()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT State, StartMode FROM Win32_Service WHERE Name='{MsiFoundationServiceName.Replace("'", "''")}'");
            using var results = searcher.Get();
            foreach (ManagementObject service in results)
            {
                var state = Convert.ToString(service["State"]) ?? string.Empty;
                var startMode = Convert.ToString(service["StartMode"]) ?? string.Empty;
                return new(true,
                    string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase),
                    !string.Equals(startMode, "Disabled", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch
        {
        }

        return new(false, false, false);
    }

    private static OemTaskState ReadScheduledTask(string taskName)
    {
        object? schedulerObject = null;
        object? folderObject = null;
        object? taskObject = null;
        try
        {
            var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            schedulerObject = schedulerType is null ? null : Activator.CreateInstance(schedulerType);
            if (schedulerObject is null)
            {
                return new(false, false, false);
            }

            dynamic scheduler = schedulerObject;
            scheduler.Connect();
            folderObject = scheduler.GetFolder("\\");
            dynamic folder = folderObject;
            taskObject = folder.GetTask(taskName);
            dynamic task = taskObject;
            return new(true, Convert.ToBoolean(task.Enabled), Convert.ToInt32(task.State) == 4);
        }
        catch
        {
            return new(false, false, false);
        }
        finally
        {
            ReleaseCom(taskObject);
            ReleaseCom(folderObject);
            ReleaseCom(schedulerObject);
        }
    }

    private static void SetMsiServiceStartMode(string startMode)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT * FROM Win32_Service WHERE Name='{MsiFoundationServiceName.Replace("'", "''")}'");
        using var results = searcher.Get();
        foreach (ManagementObject service in results)
        {
            var result = Convert.ToUInt32(service.InvokeMethod("ChangeStartMode", [startMode]));
            if (result is not 0 and not 10)
            {
                throw new InvalidOperationException($"Windows could not set {MsiFoundationServiceName} startup mode (code {result}).");
            }
        }
    }

    private static void InvokeMsiService(string method)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT * FROM Win32_Service WHERE Name='{MsiFoundationServiceName.Replace("'", "''")}'");
        using var results = searcher.Get();
        foreach (ManagementObject service in results)
        {
            var result = Convert.ToUInt32(service.InvokeMethod(method, null));
            if (result is not 0 and not 10)
            {
                throw new InvalidOperationException($"Windows could not run {method} for {MsiFoundationServiceName} (code {result}).");
            }
        }
    }

    private static void SetScheduledTaskState(string taskName, bool enabled, bool run)
    {
        object? schedulerObject = null;
        object? folderObject = null;
        object? taskObject = null;
        object? runningTaskObject = null;
        try
        {
            var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Windows Task Scheduler is not available.");
            schedulerObject = Activator.CreateInstance(schedulerType)
                ?? throw new InvalidOperationException("Windows Task Scheduler could not be opened.");
            dynamic scheduler = schedulerObject;
            scheduler.Connect();
            folderObject = scheduler.GetFolder("\\");
            dynamic folder = folderObject;
            taskObject = folder.GetTask(taskName);
            dynamic task = taskObject;
            if (!enabled && Convert.ToInt32(task.State) == 4)
            {
                task.Stop(0);
            }
            task.Enabled = enabled;
            if (enabled && run)
            {
                runningTaskObject = task.Run(null);
            }
        }
        catch (System.Runtime.InteropServices.COMException exception) when ((uint)exception.HResult == 0x80070002)
        {
        }
        finally
        {
            ReleaseCom(runningTaskObject);
            ReleaseCom(taskObject);
            ReleaseCom(folderObject);
            ReleaseCom(schedulerObject);
        }
    }

    private static void RemoveOemStartupEntries()
    {
        foreach (var entry in FindOemStartupEntries())
        {
            using var key = Registry.CurrentUser.OpenSubKey(entry.KeyPath, writable: true);
            key?.DeleteValue(entry.ValueName, throwOnMissingValue: false);
        }
    }

    private static void RestoreOemStartupEntries(IReadOnlyList<StartupEntry> entries)
    {
        foreach (var entry in entries)
        {
            using var key = Registry.CurrentUser.CreateSubKey(entry.KeyPath, writable: true);
            key.SetValue(entry.ValueName, entry.ValueData, RegistryValueKind.String);
        }
    }

    private static IReadOnlyList<QuarantinedShortcut> FindOemShortcuts(string dataDirectory)
    {
        var quarantineRoot = Path.Combine(dataDirectory, "oem-shortcut-quarantine");
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        var result = new List<QuarantinedShortcut>();
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    if (!IsMsiCenterMText(name))
                    {
                        continue;
                    }

                    var quarantinePath = Path.Combine(
                        quarantineRoot,
                        $"{result.Count:D3}-{Path.GetFileName(path)}");
                    result.Add(new(path, quarantinePath));
                }
            }
            catch
            {
            }
        }
        return result;
    }

    private static void QuarantineOemShortcuts(IReadOnlyList<QuarantinedShortcut> shortcuts)
    {
        foreach (var shortcut in shortcuts)
        {
            try
            {
                if (!File.Exists(shortcut.OriginalPath))
                {
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(shortcut.QuarantinePath)!);
                File.Move(shortcut.OriginalPath, shortcut.QuarantinePath, overwrite: true);
            }
            catch
            {
            }
        }
    }

    private static void RestoreOemShortcuts(IReadOnlyList<QuarantinedShortcut> shortcuts)
    {
        foreach (var shortcut in shortcuts)
        {
            try
            {
                if (!File.Exists(shortcut.QuarantinePath))
                {
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(shortcut.OriginalPath)!);
                File.Move(shortcut.QuarantinePath, shortcut.OriginalPath, overwrite: true);
            }
            catch
            {
            }
        }
    }

    private static MsiOemRestoreState? LoadMsiRestoreState(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<MsiOemRestoreState>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void KillMsiCenterProcesses()
    {
        foreach (var process in Process.GetProcesses().Where(IsMsiCenterMProcess))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
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

    private static bool IsAnyMsiCenterProcessRunning()
    {
        var processes = Process.GetProcesses();
        try
        {
            return processes.Any(IsMsiCenterMProcess);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); }
            catch { }
        }
    }

    private static bool IsMsiCenterMProcess(Process process)
    {
        try
        {
            var processName = process.ProcessName;
            if (IsMsiCenterMText(processName) ||
                string.Equals(processName, "MSIAPService", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(processName, "Command Center", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!ShouldInspectMsiCenterProcessMetadata(processName))
            {
                return false;
            }

            var versionInfo = process.MainModule?.FileVersionInfo;
            return IsMsiCenterMText(versionInfo?.ProductName ?? string.Empty) ||
                IsMsiCenterMText(versionInfo?.FileDescription ?? string.Empty);
        }
        catch
        {
            return false;
        }
    }

    internal static bool ShouldInspectMsiCenterProcessMetadata(string processName)
    {
        return processName.Contains("MSI", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("Center", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("QuickSettings", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("DCv2", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMsiCenterMText(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Contains("MSICenterM", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("MSIQuickSettings", StringComparison.OrdinalIgnoreCase);
    }

    private AfmfState ReadAfmfState()
    {
        try
        {
            return File.Exists(_afmfStatePath)
                ? JsonSerializer.Deserialize<AfmfState>(File.ReadAllText(_afmfStatePath), JsonOptions) ?? new()
                : new();
        }
        catch { return new(); }
    }

    private static void SendAfmfHotkey()
    {
        var inputs = new[]
        {
            KeyInput(0x12, false), KeyInput(0x10, false), KeyInput(0x47, false),
            KeyInput(0x47, true), KeyInput(0x10, true), KeyInput(0x12, true),
        };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != (uint)inputs.Length)
        {
            throw new InvalidOperationException("The AMD AFMF hotkey could not be sent.");
        }
    }

    private static INPUT KeyInput(ushort key, bool up) => new()
    {
        Type = 1,
        Data = new INPUTUNION { Keyboard = new KEYBDINPUT { VirtualKey = key, Flags = up ? 2u : 0u } }
    };

    private static int NormalizeBoostMode(uint value) => value == 0 ? 0 : 1;
    private sealed record AfmfState(bool Enabled = false);
    private sealed record StartupEntry(string KeyPath, string ValueName, string ValueData);
    private sealed record MsiOemRestoreState(
        bool ServiceStartEnabled,
        IReadOnlyList<MsiTaskRestoreState>? Tasks,
        IReadOnlyList<StartupEntry>? StartupEntries,
        IReadOnlyList<QuarantinedShortcut>? QuarantinedShortcuts = null);
    private sealed record QuarantinedShortcut(string OriginalPath, string QuarantinePath);
    private sealed record MsiTaskRestoreState(string TaskName, bool Enabled);
    private sealed record OemServiceState(bool Installed, bool Running, bool StartEnabled);
    private sealed record OemTaskState(bool Installed, bool Enabled, bool Running);
    private sealed record OemCaptureState(
        bool Active,
        string ButtonId,
        DateTimeOffset StartedAt,
        string DetectedInput,
        string StatusText);
    private sealed record OemButtonSettings(IReadOnlyList<StoredOemButtonBinding>? Bindings = null);
    private sealed record StoredOemButtonBinding(
        string ButtonId,
        string InputCode,
        string InputName,
        string ActionId,
        string CustomShortcut);

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(nint root, out nint scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(nint root, ref Guid scheme);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public INPUTUNION Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public MOUSEINPUT Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public nint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public nint ExtraInfo; }
}

public sealed record HandheldCpuBoostSnapshot(bool Supported, int AcMode, int BatteryMode, string StatusText);
public sealed record HandheldAfmfSnapshot(bool Supported, bool Enabled, string StatusText);
public sealed record HandheldOemSoftwareSnapshot(
    bool Supported,
    string DeviceId,
    string SoftwareName,
    bool Detected,
    bool AutostartEnabled,
    bool Running,
    bool ControlActive,
    bool ServiceInstalled,
    bool ServiceRunning,
    bool ServiceStartEnabled,
    bool StartupTaskInstalled,
    bool StartupTaskEnabled,
    bool StartupTaskRunning,
    bool VibrationSupported,
    int MinimumVibrationStrengthPercent,
    int MaximumVibrationStrengthPercent,
    int VibrationStrengthPercent,
    bool UiHapticsEnabled,
    string StatusText,
    IReadOnlyList<HandheldOemButtonBinding> Buttons,
    IReadOnlyList<HandheldOemActionDefinition> Actions,
    HandheldOemCaptureSnapshot Capture);

public sealed record HandheldOemButtonBinding(
    string ButtonId,
    string Title,
    string Description,
    string InputCode,
    string InputName,
    string ActionId,
    string ActionTitle,
    string CustomShortcut,
    bool Configured);

public sealed record HandheldOemActionDefinition(string Id, string Title, string Description);

public sealed record HandheldOemCaptureSnapshot(
    bool Active,
    string ButtonId,
    string DetectedInput,
    string StatusText);
