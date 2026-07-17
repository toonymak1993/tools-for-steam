using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using SteamLoader.App.Services;

namespace SteamLoader.App.Infrastructure.Handheld;

public sealed class HandheldPerformanceService
{
    private const string PawnIoSetupSha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _settingsPath;
    private readonly string _profilesPath;
    private readonly string _commandPath;
    private readonly string _statusPath;
    private readonly string _lightingPath;
    private readonly string _hapticCommandPath;
    private readonly WindowsProfileNotificationService? _notificationService;
    private readonly HandheldPowerStateReader _powerStateReader = new();
    private readonly HandheldSystemControlService _systemControls;
    private HandheldRunningGame? _currentGame;
    private HandheldPowerState _powerState = new();
    private string _lastAutomaticTargetKey = string.Empty;
    private int _lastAutomaticTdpWatts;
    private HandheldDeviceProfile? _uiHapticDevice;
    private int? _uiHapticVibrationStrength;
    private bool? _uiHapticsEnabled;

    public event Action<HandheldOemButtonBinding>? OemButtonPressed
    {
        add => _systemControls.OemButtonPressed += value;
        remove => _systemControls.OemButtonPressed -= value;
    }

    public HandheldPerformanceService(string dataDirectory)
        : this(dataDirectory, null)
    {
    }

    internal HandheldPerformanceService(
        string dataDirectory,
        WindowsProfileNotificationService? notificationService)
    {
        _settingsPath = Path.Combine(dataDirectory, "handheld-performance.json");
        _profilesPath = Path.Combine(dataDirectory, "handheld-performance-profiles.json");
        _commandPath = Path.Combine(dataDirectory, "handheld-hardware-command.json");
        _statusPath = Path.Combine(dataDirectory, "handheld-hardware-status.json");
        _lightingPath = Path.Combine(dataDirectory, "handheld-lighting.json");
        _hapticCommandPath = Path.Combine(dataDirectory, "handheld-ui-haptic.json");
        _systemControls = new HandheldSystemControlService(dataDirectory);
        _notificationService = notificationService;
        _powerState = _powerStateReader.Read(force: true);
    }

    public HandheldPerformanceSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var device = HandheldDeviceCatalog.Detect();
            var settings = LoadSettings(device);
            var profileSettings = LoadProfileSettings(device);
            var activeProfile = _currentGame is null
                ? null
                : profileSettings.Profiles?.FirstOrDefault(profile =>
                    string.Equals(profile.Key, _currentGame.Key, StringComparison.OrdinalIgnoreCase));
            var status = LoadStatus();
            var pawnIoInstalled = IsPawnIoInstalled();
            var pawnIoReady = pawnIoInstalled && status.PawnIoReady;
            var supported = HandheldDeviceCatalog.IsSupported(device);
            var statusText = !supported
                ? $"Unsupported device ({device.Manufacturer} {device.ProductCode})."
                : !IsPawnIoInstalled()
                    ? "PawnIO 2.1 or newer is required before TDP control can be enabled."
                    : status.Message;
            var globalAcWatts = ResolveSettingsTdp(settings, "ac");
            var globalBatteryWatts = ResolveSettingsTdp(settings, "battery");
            var globalWatts = ResolveSettingsTdp(settings, _powerState.PowerSource);
            var selectedWatts = activeProfile is null
                ? globalWatts
                : ResolveProfileTdp(activeProfile, _powerState.PowerSource);
            var telemetry = new HandheldPerformanceTelemetry(
                _powerState.PowerSource,
                _powerState.IsPluggedIn,
                _powerState.BatteryPercent,
                _powerState.EstimatedMinutesRemaining,
                status.AppliedTdpWatts,
                status.AppliedTdpWatts > 0 && status.AppliedTdpWatts == selectedWatts,
                _powerState.UpdatedAt);
            var lightingSettings = LoadLightingSettings(device);
            var lighting = new HandheldLightingSnapshot(
                supported && device.Lighting.Supported,
                lightingSettings.Enabled,
                lightingSettings.Effect,
                lightingSettings.LeftColor,
                lightingSettings.RightColor,
                lightingSettings.ButtonColor,
                lightingSettings.Brightness,
                status.LightingApplied,
                status.LightingMessage,
                device.Lighting.Effects);

            return new HandheldPerformanceSnapshot(
                device.DisplayName,
                device.Id,
                device.Manufacturer,
                device.ProductCode,
                supported,
                pawnIoInstalled,
                pawnIoReady,
                device.MinimumTdpWatts,
                device.MaximumTdpWatts,
                selectedWatts,
                ResolveSppt(selectedWatts),
                ResolveFppt(selectedWatts),
                status.AppliedTdpWatts,
                status.AppliedSpptWatts,
                status.AppliedFpptWatts,
                globalWatts,
                globalAcWatts,
                globalBatteryWatts,
                _powerState.PowerSource,
                telemetry,
                ResolveModeId(device, selectedWatts),
                device.Modes,
                profileSettings.AutoProfilesEnabled,
                profileSettings.NotificationsEnabled,
                _currentGame,
                activeProfile,
                (profileSettings.Profiles ?? []).OrderByDescending(profile => profile.UpdatedAt).ToArray(),
                statusText,
                status.Success || status.Nonce == 0 ? string.Empty : status.Message,
                lighting,
                _systemControls.GetCpuBoost(),
                _systemControls.GetAfmf(),
                _systemControls.GetOemSoftware(device));
        }
    }

    public HandheldPerformanceSnapshot SetCpuBoost(string powerSource, bool enabled)
    {
        lock (_sync)
        {
            _systemControls.SetCpuBoost(powerSource, enabled);
            return GetSnapshot();
        }
    }

    public HandheldPerformanceSnapshot SetAfmf(bool enabled)
    {
        lock (_sync)
        {
            _systemControls.SetAfmf(enabled);
            return GetSnapshot();
        }
    }

    public HandheldPerformanceSnapshot SetOemSoftwareEnabled(bool enabled)
    {
        lock (_sync)
        {
            var device = RequireSupportedDevice();
            if (!device.OemSoftware.Supported)
            {
                throw new InvalidOperationException("OEM software control is not supported by this device.");
            }

            WriteJsonAtomically(_commandPath, new HandheldHardwareCommand(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                device.Id,
                device.ProductCode,
                enabled ? "oem-software-enable" : "oem-software-disable",
                0));
            return GetSnapshot();
        }
    }

    public HandheldPerformanceSnapshot SetOemVibrationStrength(int strengthPercent)
    {
        lock (_sync)
        {
            _systemControls.SetVibrationStrength(RequireSupportedDevice(), strengthPercent);
            _uiHapticVibrationStrength = strengthPercent;
            return GetSnapshot();
        }
    }

    public HandheldPerformanceSnapshot SetOemUiHapticsEnabled(bool enabled)
    {
        lock (_sync)
        {
            _systemControls.SetUiHapticsEnabled(RequireSupportedDevice(), enabled);
            _uiHapticsEnabled = enabled;
            return GetSnapshot();
        }
    }

    public bool RequestUiHaptic(string kind)
    {
        lock (_sync)
        {
            var dataDirectory = Path.GetDirectoryName(_hapticCommandPath)!;
            var device = _uiHapticDevice ??= HandheldDeviceCatalog.Detect();
            if (!HandheldDeviceCatalog.IsSupported(device) || !device.Controller.VibrationSupported)
            {
                return false;
            }

            _uiHapticsEnabled ??= HandheldControllerSettingsStore.ReadUiHapticsEnabled(dataDirectory, device.Id);
            _uiHapticVibrationStrength ??= HandheldControllerSettingsStore.ReadVibrationStrengthPercent(
                dataDirectory,
                device.Id,
                device.Controller.DefaultVibrationStrengthPercent);
            if (!_uiHapticsEnabled.Value || _uiHapticVibrationStrength.Value == 0)
            {
                return false;
            }

            var command = HandheldUiHapticPatternCatalog.Create(
                device.Id,
                kind,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            WriteJsonAtomically(_hapticCommandPath, command);
            return true;
        }
    }

    public HandheldPerformanceSnapshot StartOemButtonCapture(string buttonId)
    {
        lock (_sync)
        {
            _systemControls.StartButtonCapture(RequireSupportedDevice(), buttonId);
            return GetSnapshot();
        }
    }

    public HandheldPerformanceSnapshot CancelOemButtonCapture()
    {
        lock (_sync)
        {
            _systemControls.CancelButtonCapture(RequireSupportedDevice());
            return GetSnapshot();
        }
    }

    public HandheldPerformanceSnapshot SetOemButtonBinding(
        string buttonId,
        string actionId,
        string customShortcut)
    {
        lock (_sync)
        {
            _systemControls.SetButtonBinding(RequireSupportedDevice(), buttonId, actionId, customShortcut);
            return GetSnapshot();
        }
    }

    public void ObserveOemInput(HidMenuButtonReport report)
    {
        _systemControls.ObserveOemInput(HandheldDeviceCatalog.Detect(), report);
    }

    public HandheldPerformanceSnapshot SetLighting(
        bool enabled, string effect, string leftColor, string rightColor, string buttonColor, int brightness)
    {
        lock (_sync)
        {
            var device = RequireSupportedDevice();
            if (!device.Lighting.Supported)
            {
                throw new InvalidOperationException("RGB lighting is not supported by this device.");
            }

            var normalizedEffect = effect?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!device.Lighting.Effects.Contains(normalizedEffect, StringComparer.Ordinal))
            {
                throw new ArgumentOutOfRangeException(nameof(effect), "The selected RGB effect is not supported by this device.");
            }

            var settings = new HandheldLightingSettings(
                enabled,
                normalizedEffect,
                NormalizeRgbColor(leftColor),
                NormalizeRgbColor(rightColor),
                NormalizeRgbColor(buttonColor),
                Math.Clamp(brightness, device.Lighting.MinimumBrightness, device.Lighting.MaximumBrightness),
                DateTimeOffset.UtcNow);
            WriteJsonAtomically(_lightingPath, settings);
            WriteJsonAtomically(_commandPath, new HandheldHardwareCommand(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), device.Id, device.ProductCode,
                "set-lighting", 0, settings));
            return GetSnapshot();
        }
    }

    public HandheldPerformanceSnapshot SetTdp(int watts)
    {
        lock (_sync)
        {
            var device = RequireSupportedDevice();
            if (!IsPawnIoInstalled())
            {
                throw new InvalidOperationException("PawnIO 2.1 or newer is not installed.");
            }

            var clamped = Math.Clamp(watts, device.MinimumTdpWatts, device.MaximumTdpWatts);
            var matchingMode = device.Modes.FirstOrDefault(mode => mode.Watts == clamped);
            if (_currentGame is null)
            {
                SaveGlobalPowerProfile(device, _powerState.PowerSource, clamped, matchingMode?.Id ?? "custom");
            }
            else
            {
                SaveCurrentGameProfile(device, _powerState.PowerSource, clamped);
                _lastAutomaticTargetKey = _currentGame.Key;
                _lastAutomaticTdpWatts = clamped;
            }

            WriteCommand(device, clamped);
            return GetSnapshot();
        }
    }

    public HandheldPerformanceSnapshot SetMode(string modeId)
    {
        var device = RequireSupportedDevice();
        var mode = device.Modes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modeId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected TDP mode is not supported by this device.");
        return SetTdp(mode.Watts);
    }

    public HandheldPerformanceSnapshot SetGlobalTdp(int watts, string powerSource = "")
    {
        lock (_sync)
        {
            var device = RequireSupportedDevice();
            if (!IsPawnIoInstalled())
            {
                throw new InvalidOperationException("PawnIO 2.1 or newer is not installed.");
            }

            var clamped = Math.Clamp(watts, device.MinimumTdpWatts, device.MaximumTdpWatts);
            var normalizedPowerSource = NormalizePowerSource(powerSource, _powerState.PowerSource);
            SaveGlobalPowerProfile(device, normalizedPowerSource, clamped, ResolveModeId(device, clamped));
            if (_currentGame is null && string.Equals(normalizedPowerSource, _powerState.PowerSource, StringComparison.Ordinal))
            {
                WriteCommand(device, clamped);
                _lastAutomaticTargetKey = string.Empty;
                _lastAutomaticTdpWatts = clamped;
            }

            return GetSnapshot();
        }
    }

    public HandheldPerformanceSnapshot SetGameProfileTdp(string key, int watts, string powerSource)
    {
        lock (_sync)
        {
            var device = RequireSupportedDevice();
            if (!IsPawnIoInstalled())
            {
                throw new InvalidOperationException("PawnIO 2.1 or newer is not installed.");
            }

            var settings = LoadProfileSettings(device);
            var profile = settings.Profiles?.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The selected game profile no longer exists.");
            var clamped = Math.Clamp(watts, device.MinimumTdpWatts, device.MaximumTdpWatts);
            var normalizedPowerSource = NormalizePowerSource(powerSource, _powerState.PowerSource);
            var updated = SetProfilePowerTdp(profile, normalizedPowerSource, clamped) with
            {
                UpdatedAt = DateTimeOffset.UtcNow
            };
            SaveProfileSettings(settings with
            {
                Profiles = (settings.Profiles ?? [])
                    .Where(candidate => !string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
                    .Append(updated)
                    .ToArray()
            });

            if (_currentGame is not null &&
                string.Equals(_currentGame.Key, key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(normalizedPowerSource, _powerState.PowerSource, StringComparison.Ordinal))
            {
                WriteCommand(device, clamped);
                _lastAutomaticTargetKey = key;
                _lastAutomaticTdpWatts = clamped;
            }

            return GetSnapshot();
        }
    }

    public HandheldPerformanceSnapshot SetAutoProfilesEnabled(bool enabled)
    {
        lock (_sync)
        {
            var device = RequireSupportedDevice();
            var settings = LoadProfileSettings(device) with { AutoProfilesEnabled = enabled };
            SaveProfileSettings(settings);
            _lastAutomaticTargetKey = string.Empty;
            _lastAutomaticTdpWatts = 0;
            return GetSnapshot();
        }
    }

    public HandheldPerformanceSnapshot SetProfileNotificationsEnabled(bool enabled)
    {
        lock (_sync)
        {
            var device = RequireSupportedDevice();
            SaveProfileSettings(LoadProfileSettings(device) with { NotificationsEnabled = enabled });
            return GetSnapshot();
        }
    }

    public HandheldPerformanceSnapshot ShowTestNotification()
    {
        lock (_sync)
        {
            _ = RequireSupportedDevice();
            if (_notificationService is null)
            {
                throw new InvalidOperationException("The profile notification service is not available.");
            }

            var snapshot = GetSnapshot();
            _notificationService.ShowProfileApplied(new HandheldAutomaticProfileResult(
                snapshot.CurrentGame?.Title ?? "Test game",
                snapshot.SelectedTdpWatts,
                snapshot.CurrentGame is not null,
                true));
            return snapshot;
        }
    }

    public HandheldPerformanceSnapshot DeleteProfile(string key)
    {
        lock (_sync)
        {
            var device = RequireSupportedDevice();
            var settings = LoadProfileSettings(device);
            var profiles = (settings.Profiles ?? [])
                .Where(profile => !string.Equals(profile.Key, key, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            SaveProfileSettings(settings with { Profiles = profiles });
            _lastAutomaticTargetKey = string.Empty;
            return GetSnapshot();
        }
    }

    internal HandheldAutomaticProfileResult? ApplyAutomaticProfile(HandheldRunningGame game)
    {
        lock (_sync)
        {
            var device = RequireSupportedDevice();
            _currentGame = game;
            var profileSettings = LoadProfileSettings(device);
            if (!profileSettings.AutoProfilesEnabled || !IsPawnIoInstalled())
            {
                _lastAutomaticTargetKey = string.Empty;
                return null;
            }

            var profile = profileSettings.Profiles?.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, game.Key, StringComparison.OrdinalIgnoreCase));
            var watts = profile is null
                ? ResolveSettingsTdp(LoadSettings(device), _powerState.PowerSource)
                : ResolveProfileTdp(profile, _powerState.PowerSource);
            if (string.Equals(_lastAutomaticTargetKey, game.Key, StringComparison.OrdinalIgnoreCase) &&
                _lastAutomaticTdpWatts == watts)
            {
                return null;
            }

            WriteCommand(device, watts);
            _lastAutomaticTargetKey = game.Key;
            _lastAutomaticTdpWatts = watts;
            return new HandheldAutomaticProfileResult(
                game.Title,
                watts,
                profile is not null,
                profileSettings.NotificationsEnabled);
        }
    }

    internal HandheldAutomaticProfileResult? ClearCurrentGameAndRestoreGlobal()
    {
        lock (_sync)
        {
            if (_currentGame is null)
            {
                return null;
            }

            _currentGame = null;
            var device = RequireSupportedDevice();
            var profileSettings = LoadProfileSettings(device);
            var settings = LoadSettings(device);
            var globalWatts = ResolveSettingsTdp(settings, _powerState.PowerSource);
            var shouldRestore = profileSettings.AutoProfilesEnabled &&
                IsPawnIoInstalled() &&
                (_lastAutomaticTdpWatts != globalWatts || !string.IsNullOrEmpty(_lastAutomaticTargetKey));
            _lastAutomaticTargetKey = string.Empty;
            _lastAutomaticTdpWatts = 0;
            if (!shouldRestore)
            {
                return null;
            }

            WriteCommand(device, globalWatts);
            return new HandheldAutomaticProfileResult(
                "Global profile",
                globalWatts,
                false,
                profileSettings.NotificationsEnabled);
        }
    }

    internal HandheldAutomaticProfileResult? RefreshPowerState(bool forceReapply = false)
    {
        lock (_sync)
        {
            var previousSource = _powerState.PowerSource;
            _powerState = _powerStateReader.Read(force: forceReapply);
            var sourceChanged = !string.Equals(previousSource, _powerState.PowerSource, StringComparison.Ordinal);
            if (!sourceChanged && !forceReapply)
            {
                return null;
            }

            var device = RequireSupportedDevice();
            var profileSettings = LoadProfileSettings(device);
            if (!profileSettings.AutoProfilesEnabled || !IsPawnIoInstalled())
            {
                return null;
            }

            var profile = _currentGame is null
                ? null
                : profileSettings.Profiles?.FirstOrDefault(candidate =>
                    string.Equals(candidate.Key, _currentGame.Key, StringComparison.OrdinalIgnoreCase));
            var watts = profile is null
                ? ResolveSettingsTdp(LoadSettings(device), _powerState.PowerSource)
                : ResolveProfileTdp(profile, _powerState.PowerSource);
            WriteCommand(device, watts);
            _lastAutomaticTargetKey = _currentGame?.Key ?? string.Empty;
            _lastAutomaticTdpWatts = watts;

            return new HandheldAutomaticProfileResult(
                sourceChanged
                    ? _powerState.IsPluggedIn ? "Plugged-in profile" : "Battery profile"
                    : _currentGame?.Title ?? "Global profile",
                watts,
                profile is not null,
                sourceChanged && profileSettings.NotificationsEnabled);
        }
    }

    public HandheldPerformanceSnapshot InstallOrRepairPawnIo()
    {
        lock (_sync)
        {
            _ = RequireSupportedDevice();
            var setupPath = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "PawnIO", "PawnIO_setup.exe");
            if (!File.Exists(setupPath))
            {
                throw new InvalidOperationException("The bundled PawnIO setup is missing.");
            }

            var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(setupPath)));
            if (!string.Equals(actualHash, PawnIoSetupSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("PawnIO setup failed SHA-256 verification.");
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = "-install -silent",
                WorkingDirectory = Path.GetDirectoryName(setupPath)!,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("PawnIO setup could not be started.");

            if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                throw new TimeoutException("PawnIO setup did not finish within two minutes.");
            }

            if (process.ExitCode is not (0 or 3010))
            {
                throw new InvalidOperationException($"PawnIO setup exited with code {process.ExitCode}.");
            }

            var ready = IsPawnIoInstalled();
            HandheldPerformanceService.WriteJsonAtomically(
                _statusPath,
                new HandheldHardwareStatus(
                    0,
                    ready,
                    ready,
                    0,
                    string.Empty,
                    ready ? "PawnIO 2.2.0 is installed. Select a TDP mode to test the hardware path." : "PawnIO installed, but Windows may require a restart.",
                    DateTimeOffset.UtcNow));
            return GetSnapshot();
        }
    }

    private HandheldDeviceProfile RequireSupportedDevice()
    {
        var device = HandheldDeviceCatalog.Detect();
        return HandheldDeviceCatalog.IsSupported(device)
            ? device
            : throw new InvalidOperationException("No supported handheld was detected.");
    }

    private void WriteCommand(HandheldDeviceProfile device, int watts)
    {
        WritePowerLimitsCommand(device, watts, ResolveSppt(watts), ResolveFppt(watts));
    }

    private void WritePowerLimitsCommand(HandheldDeviceProfile device, int splWatts, int spptWatts, int fpptWatts)
    {
        var command = new HandheldHardwareCommand(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            device.Id,
            device.ProductCode,
            "set-tdp",
            splWatts,
            SpptWatts: spptWatts,
            FpptWatts: fpptWatts);
        WriteJsonAtomically(_commandPath, command);
    }

    private HandheldPerformanceSettings LoadSettings(HandheldDeviceProfile device)
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var loaded = JsonSerializer.Deserialize<HandheldPerformanceSettings>(File.ReadAllText(_settingsPath), JsonOptions);
                if (loaded is not null && HandheldDeviceCatalog.IsSupported(device))
                {
                    return loaded with
                    {
                        TdpWatts = ClampTdp(device, loaded.TdpWatts),
                        AcTdpWatts = loaded.AcTdpWatts is null ? null : ClampTdp(device, loaded.AcTdpWatts.Value),
                        BatteryTdpWatts = loaded.BatteryTdpWatts is null ? null : ClampTdp(device, loaded.BatteryTdpWatts.Value)
                    };
                }
            }
        }
        catch
        {
        }

        return new HandheldPerformanceSettings();
    }

    private HandheldPerformanceProfileSettings LoadProfileSettings(HandheldDeviceProfile device)
    {
        try
        {
            if (File.Exists(_profilesPath))
            {
                var loaded = JsonSerializer.Deserialize<HandheldPerformanceProfileSettings>(
                    File.ReadAllText(_profilesPath),
                    JsonOptions);
                if (loaded is not null)
                {
                    var profiles = (loaded.Profiles ?? [])
                        .Where(profile => !string.IsNullOrWhiteSpace(profile.Key))
                        .Select(profile => profile with
                        {
                            TdpWatts = ClampTdp(device, profile.TdpWatts),
                            AcTdpWatts = profile.AcTdpWatts is null ? null : ClampTdp(device, profile.AcTdpWatts.Value),
                            BatteryTdpWatts = profile.BatteryTdpWatts is null ? null : ClampTdp(device, profile.BatteryTdpWatts.Value)
                        })
                        .GroupBy(profile => profile.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.OrderByDescending(profile => profile.UpdatedAt).First())
                        .ToArray();
                    return loaded with { Profiles = profiles };
                }
            }
        }
        catch
        {
        }

        return new HandheldPerformanceProfileSettings(Profiles: []);
    }

    private void SaveCurrentGameProfile(HandheldDeviceProfile device, string powerSource, int watts)
    {
        if (_currentGame is null)
        {
            return;
        }

        var settings = LoadProfileSettings(device);
        var existing = settings.Profiles?.FirstOrDefault(profile =>
            string.Equals(profile.Key, _currentGame.Key, StringComparison.OrdinalIgnoreCase));
        var globalSettings = LoadSettings(device);
        var profile = existing is null
            ? new HandheldGameTdpProfile(
                _currentGame.Key,
                _currentGame.AppId,
                _currentGame.Title,
                _currentGame.ExecutablePath,
                watts,
                DateTimeOffset.UtcNow,
                string.Equals(powerSource, "ac", StringComparison.Ordinal)
                    ? watts
                    : ResolveSettingsTdp(globalSettings, "ac"),
                string.Equals(powerSource, "battery", StringComparison.Ordinal)
                    ? watts
                    : ResolveSettingsTdp(globalSettings, "battery"))
            : SetProfilePowerTdp(existing, powerSource, watts) with
            {
                AppId = _currentGame.AppId,
                Title = _currentGame.Title,
                ExecutablePath = _currentGame.ExecutablePath,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        var profiles = (settings.Profiles ?? [])
            .Where(profile => !string.Equals(profile.Key, _currentGame.Key, StringComparison.OrdinalIgnoreCase))
            .Append(profile)
            .ToArray();
        SaveProfileSettings(settings with { Profiles = profiles });
    }

    private void SaveGlobalPowerProfile(
        HandheldDeviceProfile device,
        string powerSource,
        int watts,
        string modeId)
    {
        var settings = LoadSettings(device);
        var currentAcWatts = ResolveSettingsTdp(settings, "ac");
        var currentBatteryWatts = ResolveSettingsTdp(settings, "battery");
        var updated = string.Equals(powerSource, "battery", StringComparison.Ordinal)
            ? settings with { AcTdpWatts = currentAcWatts, BatteryTdpWatts = watts }
            : settings with { AcTdpWatts = watts, BatteryTdpWatts = currentBatteryWatts };
        SaveSettings(updated with
        {
            TdpWatts = watts,
            ModeId = modeId,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    internal static int ResolveSettingsTdp(HandheldPerformanceSettings settings, string powerSource) =>
        string.Equals(powerSource, "battery", StringComparison.OrdinalIgnoreCase)
            ? settings.BatteryTdpWatts ?? settings.TdpWatts
            : settings.AcTdpWatts ?? settings.TdpWatts;

    internal static int ResolveProfileTdp(HandheldGameTdpProfile profile, string powerSource) =>
        string.Equals(powerSource, "battery", StringComparison.OrdinalIgnoreCase)
            ? profile.BatteryTdpWatts ?? profile.TdpWatts
            : profile.AcTdpWatts ?? profile.TdpWatts;

    private static HandheldGameTdpProfile SetProfilePowerTdp(
        HandheldGameTdpProfile profile,
        string powerSource,
        int watts)
    {
        var currentAcWatts = ResolveProfileTdp(profile, "ac");
        var currentBatteryWatts = ResolveProfileTdp(profile, "battery");
        return string.Equals(powerSource, "battery", StringComparison.Ordinal)
            ? profile with
            {
                TdpWatts = watts,
                AcTdpWatts = currentAcWatts,
                BatteryTdpWatts = watts
            }
            : profile with
            {
                TdpWatts = watts,
                AcTdpWatts = watts,
                BatteryTdpWatts = currentBatteryWatts
            };
    }

    private static string NormalizePowerSource(string powerSource, string fallback) =>
        string.Equals(powerSource, "battery", StringComparison.OrdinalIgnoreCase)
            ? "battery"
            : string.Equals(powerSource, "ac", StringComparison.OrdinalIgnoreCase)
                ? "ac"
                : fallback;

    private static int ClampTdp(HandheldDeviceProfile device, int watts) =>
        Math.Clamp(watts, device.MinimumTdpWatts, device.MaximumTdpWatts);

    private static int ResolveSppt(int splWatts) => Math.Min(40, splWatts + 5);

    private static int ResolveFppt(int splWatts) => Math.Min(48, splWatts + 13);

    private static string ResolveModeId(HandheldDeviceProfile device, int watts) =>
        device.Modes.FirstOrDefault(mode => mode.Watts == watts)?.Id ?? "custom";

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

    private HandheldLightingSettings LoadLightingSettings(HandheldDeviceProfile device)
    {
        try
        {
            if (File.Exists(_lightingPath))
            {
                return JsonSerializer.Deserialize<HandheldLightingSettings>(File.ReadAllText(_lightingPath), JsonOptions) ?? new();
            }
        }
        catch
        {
        }

        return new HandheldLightingSettings(
            Brightness: device.Lighting.Supported ? device.Lighting.MaximumBrightness : 0);
    }

    private static string NormalizeRgbColor(string value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length != 7 || normalized[0] != '#' ||
            !normalized.AsSpan(1).ToString().All(Uri.IsHexDigit))
        {
            throw new ArgumentException("RGB colors must use #RRGGBB format.");
        }
        return normalized;
    }

    private void SaveSettings(HandheldPerformanceSettings settings) => WriteJsonAtomically(_settingsPath, settings);

    private void SaveProfileSettings(HandheldPerformanceProfileSettings settings) =>
        WriteJsonAtomically(_profilesPath, settings);

    internal static void WriteJsonAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporaryPath, path, true);
    }

    internal static bool IsPawnIoInstalled()
    {
        return IsPawnIoInstalled(Microsoft.Win32.RegistryView.Registry64) ||
            IsPawnIoInstalled(Microsoft.Win32.RegistryView.Registry32);
    }

    private static bool IsPawnIoInstalled(Microsoft.Win32.RegistryView view)
    {
        try
        {
            using var localMachine = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
            using var key = localMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            return key?.GetSubKeyNames().Any(name =>
            {
                using var item = key.OpenSubKey(name);
                return item?.GetValue("DisplayName") is string displayName &&
                    displayName.Contains("PawnIO", StringComparison.OrdinalIgnoreCase) &&
                    Version.TryParse(Convert.ToString(item.GetValue("DisplayVersion")), out var version) &&
                    version >= new Version(2, 1);
            }) == true;
        }
        catch
        {
            return false;
        }
    }
}
