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
    private readonly HandheldDeviceProfile _device;
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
        _device = HandheldDeviceCatalog.Detect();
        _notificationService = notificationService;
        _powerState = _powerStateReader.Read(force: true);
    }

    public HandheldPerformanceSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var device = _device;
            var settings = LoadSettings(device);
            var profileSettings = LoadProfileSettings(device);
            var activeProfile = _currentGame is null
                ? null
                : profileSettings.Profiles?.FirstOrDefault(profile =>
                    string.Equals(profile.Key, _currentGame.Key, StringComparison.OrdinalIgnoreCase));
            var status = LoadStatus();
            var pawnIoInstalled = IsPawnIoInstalled();
            var pawnIoReady = pawnIoInstalled && status.PawnIoReady;
            var supported = HandheldDeviceCatalog.SupportsTdp(device);
            var vendorApps = HandheldVendorAppDetector.FindRunning();
            var statusText = !supported
                ? $"Unsupported device ({device.Manufacturer} {device.ProductCode})."
                : !pawnIoInstalled
                    ? "PawnIO 2.1 or newer is required before TDP control can be enabled."
                    : vendorApps.Count > 0
                        ? $"{string.Join(", ", vendorApps)} is running and may override the TDP."
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
            var cpuBoost = supported
                ? _systemControls.GetCpuBoost()
                : new HandheldCpuBoostSnapshot(
                    false,
                    0,
                    0,
                    "CPU boost control is not available for this device.");
            var afmf = supported
                ? _systemControls.GetAfmf()
                : new HandheldAfmfSnapshot(
                    false,
                    false,
                    "AFMF control is not available for this device.");
            var oemSoftware = _systemControls.GetOemSoftware(device);

            return new HandheldPerformanceSnapshot(
                device.DisplayName,
                device.Id,
                device.Manufacturer,
                device.ProductCode,
                supported,
                pawnIoInstalled,
                pawnIoReady,
                device.MinimumTdpWatts,
                MaximumTdp(device, _powerState.PowerSource),
                selectedWatts,
                ResolveSppt(device, selectedWatts, _powerState.PowerSource),
                ResolveFppt(device, selectedWatts, _powerState.PowerSource),
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
                cpuBoost,
                afmf,
                oemSoftware);
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
            var device = RequireOemDevice();
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
            _systemControls.SetVibrationStrength(RequireOemDevice(), strengthPercent);
            _uiHapticVibrationStrength = strengthPercent;
            return GetSnapshot();
        }
    }

    public HandheldPerformanceSnapshot SetOemUiHapticsEnabled(bool enabled)
    {
        lock (_sync)
        {
            _systemControls.SetUiHapticsEnabled(RequireOemDevice(), enabled);
            _uiHapticsEnabled = enabled;
            return GetSnapshot();
        }
    }

    public bool RequestUiHaptic(string kind)
    {
        lock (_sync)
        {
            var dataDirectory = Path.GetDirectoryName(_hapticCommandPath)!;
            var device = _uiHapticDevice ??= _device;
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
            _systemControls.StartButtonCapture(RequireOemDevice(), buttonId);
            return GetSnapshot();
        }
    }

    public HandheldPerformanceSnapshot CancelOemButtonCapture()
    {
        lock (_sync)
        {
            _systemControls.CancelButtonCapture(RequireOemDevice());
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
            _systemControls.SetButtonBinding(RequireOemDevice(), buttonId, actionId, customShortcut);
            return GetSnapshot();
        }
    }

    public void ObserveOemInput(HidMenuButtonReport report)
    {
        _systemControls.ObserveOemInput(_device, report);
    }

    public HandheldPerformanceSnapshot SetLighting(
        bool enabled, string effect, string leftColor, string rightColor, string buttonColor, int brightness)
    {
        lock (_sync)
        {
            var device = RequireOemDevice();
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

            var clamped = ClampTdp(device, watts, _powerState.PowerSource);
            var matchingMode = device.Modes.FirstOrDefault(mode => mode.Watts == clamped);
            // Like the Steam Deck: changes go into the running game's profile only when
            // that game uses its own profile; otherwise they change the global value.
            if (_currentGame is null || FindCurrentGameProfile(device) is null)
            {
                SaveGlobalPowerProfile(device, _powerState.PowerSource, clamped, matchingMode?.Id ?? "custom");
                _lastAutomaticTargetKey = _currentGame?.Key ?? string.Empty;
                _lastAutomaticTdpWatts = clamped;
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

            var normalizedPowerSource = NormalizePowerSource(powerSource, _powerState.PowerSource);
            var clamped = ClampTdp(device, watts, normalizedPowerSource);
            SaveGlobalPowerProfile(device, normalizedPowerSource, clamped, ResolveModeId(device, clamped));
            if ((_currentGame is null || FindCurrentGameProfile(device) is null) &&
                string.Equals(normalizedPowerSource, _powerState.PowerSource, StringComparison.Ordinal))
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
            var normalizedPowerSource = NormalizePowerSource(powerSource, _powerState.PowerSource);
            var clamped = ClampTdp(device, watts, normalizedPowerSource);
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
            NotifyActiveProfile(enabled ? FindCurrentGameProfile(device) : null);
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
            if (string.Equals(_activeProfileKey, key, StringComparison.OrdinalIgnoreCase))
            {
                NotifyActiveProfile(null);
            }

            return GetSnapshot();
        }
    }

    /// <summary>Raised when the game profile in effect changes (null = global values).</summary>
    public event Action<HandheldGameTdpProfile?>? ActiveGameProfileChanged;

    private string _activeProfileKey = string.Empty;
    private GameProfileExtras? _activeProfileExtras;

    private void NotifyActiveProfile(HandheldGameTdpProfile? profile, bool force = false)
    {
        var key = profile?.Key ?? string.Empty;
        if (!force &&
            string.Equals(key, _activeProfileKey, StringComparison.OrdinalIgnoreCase) &&
            Equals(profile?.Extras, _activeProfileExtras))
        {
            return;
        }

        _activeProfileKey = key;
        _activeProfileExtras = profile?.Extras;
        try
        {
            ActiveGameProfileChanged?.Invoke(profile);
        }
        catch
        {
            // A failing listener must not stop TDP handling.
        }
    }

    public HandheldGameTdpProfile? GetProfile(string key)
    {
        lock (_sync)
        {
            var device = RequireSupportedDevice();
            return LoadProfileSettings(device).Profiles?.FirstOrDefault(profile =>
                string.Equals(profile.Key, key, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>How long the elevated helper may take before "no answer" is reported.</summary>
    internal static readonly TimeSpan HelperAnswerTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Compares the last TDP command with the elevated helper's answer.</summary>
    public HandheldTdpConfirmation GetTdpConfirmation()
    {
        lock (_sync)
        {
            return ResolveTdpConfirmation(LoadCommand(), LoadStatus(), DateTimeOffset.UtcNow);
        }
    }

    internal static HandheldTdpConfirmation ResolveTdpConfirmation(
        HandheldHardwareCommand? command,
        HandheldHardwareStatus status,
        DateTimeOffset now)
    {
        var tdpCommand = command is not null && string.Equals(command.Operation, "set-tdp", StringComparison.Ordinal)
            ? command
            : null;
        if (tdpCommand is not null && status.Nonce < tdpCommand.Nonce)
        {
            var sentAt = DateTimeOffset.FromUnixTimeMilliseconds(tdpCommand.Nonce);
            return now - sentAt > HelperAnswerTimeout
                ? new HandheldTdpConfirmation("no-helper", tdpCommand.TdpWatts, tdpCommand.SpptWatts, tdpCommand.FpptWatts, string.Empty,
                    "The elevated hardware helper did not answer.", sentAt)
                : new HandheldTdpConfirmation("pending", tdpCommand.TdpWatts, tdpCommand.SpptWatts, tdpCommand.FpptWatts, string.Empty,
                    string.Empty, sentAt);
        }

        if (string.Equals(status.Operation, "set-tdp", StringComparison.Ordinal) && status.Nonce > 0 && !status.Success)
        {
            return new HandheldTdpConfirmation("failed", tdpCommand?.TdpWatts ?? 0, 0, 0, string.Empty, status.Message, status.UpdatedAt);
        }

        return status.AppliedTdpWatts > 0
            ? new HandheldTdpConfirmation("confirmed", status.AppliedTdpWatts, status.AppliedSpptWatts, status.AppliedFpptWatts,
                status.CpuCodeName, status.Message, status.UpdatedAt)
            : new HandheldTdpConfirmation("none", 0, 0, 0, string.Empty, string.Empty, null);
    }

    private HandheldHardwareCommand? LoadCommand()
    {
        try
        {
            return File.Exists(_commandPath)
                ? JsonSerializer.Deserialize<HandheldHardwareCommand>(File.ReadAllText(_commandPath), JsonOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Used by the game monitor: hand-added apps count as "games" for their profile.</summary>
    public bool HasProfile(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        lock (_sync)
        {
            return HandheldDeviceCatalog.SupportsTdp(_device) &&
                (LoadProfileSettings(_device).Profiles?.Any(profile =>
                    string.Equals(profile.Key, key, StringComparison.OrdinalIgnoreCase)) ?? false);
        }
    }

    /// <summary>
    /// "Add profile": creates a profile for a game or app the user picked, starting from the
    /// global TDP values. Profiles are only ever created this way or with "use per-game profile".
    /// </summary>
    public HandheldGameTdpProfile CreateProfile(string key, string appId, string title, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("No game or app was selected.");
        }

        lock (_sync)
        {
            var device = RequireSupportedDevice();
            var settings = LoadProfileSettings(device);
            var existing = settings.Profiles?.FirstOrDefault(profile =>
                string.Equals(profile.Key, key, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            var global = LoadSettings(device);
            var profile = new HandheldGameTdpProfile(
                key.Trim(),
                appId ?? string.Empty,
                string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(executablePath ?? string.Empty) : title.Trim(),
                executablePath ?? string.Empty,
                ResolveSettingsTdp(global, _powerState.PowerSource),
                DateTimeOffset.UtcNow,
                ResolveSettingsTdp(global, "ac"),
                ResolveSettingsTdp(global, "battery"));
            SaveProfileSettings(settings with { Profiles = (settings.Profiles ?? []).Append(profile).ToArray() });
            if (_currentGame is not null && string.Equals(_currentGame.Key, profile.Key, StringComparison.OrdinalIgnoreCase))
            {
                _lastAutomaticTargetKey = string.Empty;
                NotifyActiveProfile(profile);
            }

            return profile;
        }
    }

    /// <summary>The running game's profile while it is in effect, else null.</summary>
    public HandheldGameTdpProfile? GetActiveProfile()
    {
        lock (_sync)
        {
            return string.IsNullOrEmpty(_activeProfileKey) || !HandheldDeviceCatalog.SupportsTdp(_device)
                ? null
                : GetProfile(_activeProfileKey);
        }
    }

    /// <summary>
    /// Saves a profile edited on its settings page. When it belongs to the running game,
    /// the new TDP and settings take effect at once.
    /// </summary>
    public HandheldPerformanceSnapshot UpdateProfile(string key, int acWatts, int batteryWatts, GameProfileExtras? extras)
    {
        lock (_sync)
        {
            var device = RequireSupportedDevice();
            var settings = LoadProfileSettings(device);
            var profile = settings.Profiles?.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The selected game profile no longer exists.");
            var ac = ClampTdp(device, acWatts, "ac");
            var battery = ClampTdp(device, batteryWatts, "battery");
            var normalizedExtras = NormalizeExtras(extras);
            var updated = profile with
            {
                TdpWatts = string.Equals(_powerState.PowerSource, "battery", StringComparison.Ordinal) ? battery : ac,
                AcTdpWatts = ac,
                BatteryTdpWatts = battery,
                Extras = normalizedExtras,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            SaveProfileSettings(settings with
            {
                Profiles = (settings.Profiles ?? [])
                    .Where(candidate => !string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
                    .Append(updated)
                    .ToArray()
            });

            if (string.Equals(_activeProfileKey, key, StringComparison.OrdinalIgnoreCase))
            {
                var watts = ResolveProfileTdp(updated, _powerState.PowerSource);
                if (IsPawnIoInstalled())
                {
                    WriteCommand(device, watts);
                }

                _lastAutomaticTargetKey = key;
                _lastAutomaticTdpWatts = watts;
                NotifyActiveProfile(updated);
            }

            return GetSnapshot();
        }
    }

    /// <summary>Stores one extra value in the running game's profile (used while "use per-game profile" is on).</summary>
    public bool TryUpdateActiveProfileExtras(Func<GameProfileExtras, GameProfileExtras> change)
    {
        lock (_sync)
        {
            if (string.IsNullOrEmpty(_activeProfileKey) || !HandheldDeviceCatalog.SupportsTdp(_device))
            {
                return false;
            }

            var settings = LoadProfileSettings(_device);
            var profile = settings.Profiles?.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, _activeProfileKey, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                return false;
            }

            var updated = profile with
            {
                Extras = NormalizeExtras(change(profile.Extras ?? new GameProfileExtras())),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            SaveProfileSettings(settings with
            {
                Profiles = (settings.Profiles ?? [])
                    .Where(candidate => !string.Equals(candidate.Key, profile.Key, StringComparison.OrdinalIgnoreCase))
                    .Append(updated)
                    .ToArray()
            });
            // The caller already applied the value; only remember the new extras.
            _activeProfileExtras = updated.Extras;
            return true;
        }
    }

    internal static GameProfileExtras? NormalizeExtras(GameProfileExtras? extras)
    {
        if (extras is null)
        {
            return null;
        }

        var powerMode = extras.PowerMode?.Trim().ToLowerInvariant();
        var normalized = new GameProfileExtras(
            extras.FrameLimit is null ? null : Math.Clamp(extras.FrameLimit.Value, 0, 500),
            extras.RefreshRate is null or <= 0 ? null : Math.Clamp(extras.RefreshRate.Value, 24, 500),
            extras.RtssLevel is null ? null : Math.Clamp(extras.RtssLevel.Value, 0, 10),
            powerMode is "efficiency" or "balanced" or "performance" ? powerMode : null,
            extras.CpuBoost);
        return normalized.IsEmpty ? null : normalized;
    }

    internal HandheldAutomaticProfileResult? ApplyAutomaticProfile(HandheldRunningGame game)
    {
        lock (_sync)
        {
            var device = RequireSupportedDevice();
            _currentGame = game;
            var profileSettings = LoadProfileSettings(device);
            var profile = profileSettings.Profiles?.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, game.Key, StringComparison.OrdinalIgnoreCase));
            // Frame limit, refresh rate, RTSS etc. follow the profile even without PawnIO.
            NotifyActiveProfile(profileSettings.AutoProfilesEnabled ? profile : null);
            if (!profileSettings.AutoProfilesEnabled || !IsPawnIoInstalled() || !HasUserTdpChoice(profileSettings))
            {
                _lastAutomaticTargetKey = string.Empty;
                return null;
            }
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
            NotifyActiveProfile(null);
            var device = RequireSupportedDevice();
            var profileSettings = LoadProfileSettings(device);
            var settings = LoadSettings(device);
            var globalWatts = ResolveSettingsTdp(settings, _powerState.PowerSource);
            var shouldRestore = profileSettings.AutoProfilesEnabled &&
                IsPawnIoInstalled() &&
                HasUserTdpChoice(profileSettings) &&
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
            if (!profileSettings.AutoProfilesEnabled || !IsPawnIoInstalled() || !HasUserTdpChoice(profileSettings))
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

    /// <summary>TDP and game-profile features: any device with a known AMD TDP range.</summary>
    private HandheldDeviceProfile RequireSupportedDevice()
    {
        var device = _device;
        return HandheldDeviceCatalog.SupportsTdp(device)
            ? device
            : throw new InvalidOperationException("No AMD processor with a known TDP range was detected.");
    }

    /// <summary>OEM buttons, RGB and vibration: only fully supported handhelds.</summary>
    private HandheldDeviceProfile RequireOemDevice()
    {
        var device = _device;
        return HandheldDeviceCatalog.IsSupported(device)
            ? device
            : throw new InvalidOperationException("No supported handheld was detected.");
    }

    /// <summary>
    /// TFS leaves the processor's own limits alone until the user picked a TDP once
    /// (global setting or a game profile). Without that, automatic profiles write nothing.
    /// </summary>
    private bool HasUserTdpChoice(HandheldPerformanceProfileSettings profileSettings)
        => File.Exists(_settingsPath) || (profileSettings.Profiles?.Count ?? 0) > 0;

    private void WriteCommand(HandheldDeviceProfile device, int watts)
    {
        var source = _powerState.PowerSource;
        watts = ClampTdp(device, watts, source);
        WritePowerLimitsCommand(device, watts, ResolveSppt(device, watts, source), ResolveFppt(device, watts, source));
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
                if (loaded is not null && HandheldDeviceCatalog.SupportsTdp(device))
                {
                    return loaded with
                    {
                        TdpWatts = ClampTdp(device, loaded.TdpWatts),
                        AcTdpWatts = loaded.AcTdpWatts is null ? null : ClampTdp(device, loaded.AcTdpWatts.Value, "ac"),
                        BatteryTdpWatts = ClampTdp(device, loaded.BatteryTdpWatts ?? loaded.TdpWatts, "battery")
                    };
                }
            }
        }
        catch
        {
        }

        return device.Tdp is null
            ? new HandheldPerformanceSettings()
            : new HandheldPerformanceSettings(device.Tdp.DefaultWatts, ResolveModeId(device, device.Tdp.DefaultWatts));
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
                            AcTdpWatts = profile.AcTdpWatts is null ? null : ClampTdp(device, profile.AcTdpWatts.Value, "ac"),
                            BatteryTdpWatts = ClampTdp(device, profile.BatteryTdpWatts ?? profile.TdpWatts, "battery")
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

    private static int MaximumTdp(HandheldDeviceProfile device, string powerSource) =>
        device.Tdp?.MaximumFor(powerSource) ?? device.MaximumTdpWatts;

    private static int ClampTdp(HandheldDeviceProfile device, int watts, string powerSource = "ac") =>
        Math.Clamp(watts, device.MinimumTdpWatts, Math.Max(device.MinimumTdpWatts, MaximumTdp(device, powerSource)));

    private static int ResolveSppt(HandheldDeviceProfile device, int splWatts, string powerSource = "ac")
        => device.Tdp?.ResolveSppt(splWatts, powerSource) ?? splWatts;

    private static int ResolveFppt(HandheldDeviceProfile device, int splWatts, string powerSource = "ac")
        => device.Tdp?.ResolveFppt(splWatts, powerSource) ?? splWatts;

    private HandheldGameTdpProfile? FindCurrentGameProfile(HandheldDeviceProfile device)
        => _currentGame is null
            ? null
            : LoadProfileSettings(device).Profiles?.FirstOrDefault(profile =>
                string.Equals(profile.Key, _currentGame.Key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Steam Deck's "use per-game profile": on copies the current values into a profile for
    /// the running game; off deletes that profile and goes back to the global values.
    /// </summary>
    public HandheldPerformanceSnapshot SetCurrentGameProfileEnabled(bool enabled)
    {
        lock (_sync)
        {
            var device = RequireSupportedDevice();
            var game = _currentGame ?? throw new InvalidOperationException("No game is running.");
            var settings = LoadProfileSettings(device);
            var existing = FindCurrentGameProfile(device);
            if (enabled && existing is null)
            {
                var global = LoadSettings(device);
                SaveProfileSettings(settings with
                {
                    Profiles = (settings.Profiles ?? []).Append(new HandheldGameTdpProfile(
                        game.Key,
                        game.AppId,
                        game.Title,
                        game.ExecutablePath,
                        ResolveSettingsTdp(global, _powerState.PowerSource),
                        DateTimeOffset.UtcNow,
                        ResolveSettingsTdp(global, "ac"),
                        ResolveSettingsTdp(global, "battery"))).ToArray()
                });
                _lastAutomaticTargetKey = game.Key;
                NotifyActiveProfile(FindCurrentGameProfile(device));
            }
            else if (!enabled && existing is not null)
            {
                SaveProfileSettings(settings with
                {
                    Profiles = (settings.Profiles ?? [])
                        .Where(profile => !string.Equals(profile.Key, game.Key, StringComparison.OrdinalIgnoreCase))
                        .ToArray()
                });
                var globalWatts = ResolveSettingsTdp(LoadSettings(device), _powerState.PowerSource);
                if (IsPawnIoInstalled() && HasUserTdpChoice(LoadProfileSettings(device)))
                {
                    WriteCommand(device, globalWatts);
                }

                _lastAutomaticTargetKey = game.Key;
                _lastAutomaticTdpWatts = globalWatts;
                NotifyActiveProfile(null);
            }

            return GetSnapshot();
        }
    }

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
