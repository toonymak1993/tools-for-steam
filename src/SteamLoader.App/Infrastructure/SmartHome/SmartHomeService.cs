using System.Text.Json;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.SmartHome;

public sealed class SmartHomeService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(2);
    private static readonly string[] CapabilityControlOrder =
    [
        "onoff",
        "dim",
        "light_hue",
        "light_saturation",
        "light_temperature"
    ];

    private readonly SmartHomeSettingsStore _settingsStore;
    private readonly HomeySmartHomeClient _homeyClient;
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);

    private SmartHomeSnapshot? _cachedSnapshot;
    private DateTimeOffset _cachedSnapshotAtUtc = DateTimeOffset.MinValue;

    public SmartHomeService(HttpClient httpClient, SmartHomeSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        _homeyClient = new HomeySmartHomeClient(httpClient);
    }

    public async Task<SmartHomeSnapshot> GetSnapshotAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _snapshotGate.WaitAsync(cancellationToken);
        try
        {
            var configuration = _settingsStore.Load();
            if (!forceRefresh &&
                _cachedSnapshot is not null &&
                DateTimeOffset.UtcNow - _cachedSnapshotAtUtc < CacheDuration)
            {
                return _cachedSnapshot;
            }

            var snapshot = await RefreshSnapshotCoreAsync(configuration, cancellationToken);
            _cachedSnapshot = snapshot;
            _cachedSnapshotAtUtc = DateTimeOffset.UtcNow;
            return snapshot;
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    public async Task<SmartHomeSnapshot> SetHomeyBaseUrlAsync(string? baseUrl, CancellationToken cancellationToken)
    {
        var configuration = _settingsStore.Load();
        configuration.Homey.BaseUrl = SmartHomeSettingsStore.NormalizeBaseUrl(baseUrl);
        _settingsStore.Save(configuration);
        InvalidateSnapshot();
        return await GetSnapshotAsync(forceRefresh: true, cancellationToken);
    }

    public async Task<SmartHomeSnapshot> SetHomeyHomeyIdAsync(string? homeyId, CancellationToken cancellationToken)
    {
        var configuration = _settingsStore.Load();
        configuration.Homey.HomeyId = NormalizeText(homeyId);
        _settingsStore.Save(configuration);
        InvalidateSnapshot();
        return await GetSnapshotAsync(forceRefresh: false, cancellationToken);
    }

    public async Task<SmartHomeSnapshot> SetHomeySessionTokenAsync(string? sessionToken, CancellationToken cancellationToken)
    {
        var configuration = _settingsStore.Load();
        configuration.Homey.SessionToken = NormalizeText(sessionToken);
        _settingsStore.Save(configuration);
        InvalidateSnapshot();
        return await GetSnapshotAsync(forceRefresh: true, cancellationToken);
    }

    public async Task<SmartHomeSnapshot> ClearHomeySessionTokenAsync(CancellationToken cancellationToken)
    {
        var configuration = _settingsStore.Load();
        configuration.Homey.SessionToken = string.Empty;
        _settingsStore.Save(configuration);
        InvalidateSnapshot();
        return await GetSnapshotAsync(forceRefresh: false, cancellationToken);
    }

    public async Task<SmartHomeSnapshot> SetDeviceCapabilityAsync(
        string deviceId,
        string capabilityId,
        JsonElement value,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new InvalidOperationException("A Homey device id is required.");
        }

        if (string.IsNullOrWhiteSpace(capabilityId))
        {
            throw new InvalidOperationException("A Homey capability id is required.");
        }

        var configuration = _settingsStore.Load();
        EnsureHomeyConfigured(configuration);

        await _homeyClient.SetCapabilityValueAsync(configuration.Homey, deviceId, capabilityId, value, cancellationToken);

        await _snapshotGate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedSnapshot is not null)
            {
                _cachedSnapshot = PatchCapabilityValue(_cachedSnapshot, deviceId, capabilityId, value);
                _cachedSnapshotAtUtc = DateTimeOffset.UtcNow;
                return _cachedSnapshot;
            }
        }
        finally
        {
            _snapshotGate.Release();
        }

        return await GetSnapshotAsync(forceRefresh: true, cancellationToken);
    }

    public async Task<SmartHomeSnapshot> TriggerFlowAsync(
        string flowId,
        bool isAdvanced,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(flowId))
        {
            throw new InvalidOperationException("A Homey flow id is required.");
        }

        var configuration = _settingsStore.Load();
        EnsureHomeyConfigured(configuration);
        await _homeyClient.TriggerFlowAsync(configuration.Homey, flowId, isAdvanced, cancellationToken);
        return await GetSnapshotAsync(forceRefresh: false, cancellationToken);
    }

    public async Task<SmartHomeSnapshot> ApplyMoodAsync(
        string moodId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(moodId))
        {
            throw new InvalidOperationException("A Homey mood id is required.");
        }

        var configuration = _settingsStore.Load();
        EnsureHomeyConfigured(configuration);
        await _homeyClient.SetMoodAsync(configuration.Homey, moodId, cancellationToken);
        return await GetSnapshotAsync(forceRefresh: true, cancellationToken);
    }

    private async Task<SmartHomeSnapshot> RefreshSnapshotCoreAsync(
        SmartHomeConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var settings = BuildSettingsState(
            configuration,
            isConnected: false,
            statusText: BuildConnectionStatusText(configuration.Homey, isConnected: false, errorText: string.Empty));
        if (!settings.Homey.IsConfigured)
        {
            return BuildDisconnectedSnapshot(
                settings,
                "Add a Homey address and session token to discover rooms, devices, and flows.",
                string.Empty,
                preserveCatalog: _cachedSnapshot);
        }

        if (!string.Equals(configuration.ProviderId, SmartHomeProviderIds.Homey, StringComparison.Ordinal))
        {
            return BuildDisconnectedSnapshot(
                settings,
                "The selected smart-home provider is not implemented yet.",
                string.Empty,
                preserveCatalog: _cachedSnapshot);
        }

        try
        {
            var catalog = await _homeyClient.GetCatalogAsync(configuration.Homey, cancellationToken);
            return BuildHomeySnapshot(configuration, catalog, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            var errorText = exception.Message;
            var failureSettings = BuildSettingsState(
                configuration,
                isConnected: false,
                statusText: BuildConnectionStatusText(configuration.Homey, isConnected: false, errorText));
            return BuildDisconnectedSnapshot(
                failureSettings,
                "Homey is configured, but the latest refresh failed.",
                errorText,
                preserveCatalog: _cachedSnapshot);
        }
    }

    private static SmartHomeSnapshot BuildHomeySnapshot(
        SmartHomeConfiguration configuration,
        HomeyCatalogData catalog,
        DateTimeOffset refreshedAtUtc)
    {
        var devices = catalog.Devices.Values
            .Select(device => MapDevice(device, catalog.Zones))
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var devicesByZoneId = devices
            .Where(device => !string.IsNullOrWhiteSpace(device.ZoneId))
            .GroupBy(device => device.ZoneId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<SmartHomeDeviceState>)group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var zones = catalog.Zones.Values
            .Where(zone => devicesByZoneId.ContainsKey(zone.Id))
            .Select(zone =>
            {
                var zoneDevices = devicesByZoneId.TryGetValue(zone.Id, out var currentDevices)
                    ? currentDevices
                    : [];
                return new SmartHomeZoneState(
                    zone.Id,
                    string.IsNullOrWhiteSpace(zone.Name) ? "Room" : zone.Name,
                    zone.ParentId,
                    ResolveZonePath(catalog.Zones, zone.Id),
                    ResolveZoneDepth(catalog.Zones, zone.Id),
                    zoneDevices.Count,
                    zoneDevices.Count(device => device.IsLight),
                    zoneDevices);
            })
            .OrderBy(zone => zone.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(zone => zone.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var unassignedDevices = devices
            .Where(device => string.IsNullOrWhiteSpace(device.ZoneId) || !catalog.Zones.ContainsKey(device.ZoneId))
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var flows = catalog.Flows.Values
            .Concat(catalog.AdvancedFlows.Values)
            .Select(MapFlow)
            .OrderBy(flow => flow.IsAdvanced ? 1 : 0)
            .ThenBy(flow => flow.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var moods = catalog.Moods.Values
            .Select(mood => MapMood(mood, catalog.Zones))
            .OrderBy(mood => mood.ZonePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mood => mood.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var settings = BuildSettingsState(
            configuration,
            isConnected: true,
            statusText: BuildConnectionStatusText(configuration.Homey, isConnected: true, errorText: string.Empty));
        var overview = BuildOverview(zones, unassignedDevices, flows, moods);
        var flowCountText = overview.FlowCount == 1 ? "1 flow" : $"{overview.FlowCount} flows";
        var moodCountText = overview.MoodCount == 1 ? "1 mood" : $"{overview.MoodCount} moods";
        var deviceCountText = overview.DeviceCount == 1 ? "1 device" : $"{overview.DeviceCount} devices";
        var zoneCountText = overview.ZoneCount == 1 ? "1 room" : $"{overview.ZoneCount} rooms";

        return new SmartHomeSnapshot(
            settings,
            overview,
            $"Connected to Homey. {zoneCountText}, {deviceCountText}, {flowCountText}, {moodCountText} ready.",
            string.Empty,
            refreshedAtUtc,
            zones,
            unassignedDevices,
            flows,
            moods);
    }

    private static SmartHomeSnapshot BuildDisconnectedSnapshot(
        SmartHomeSettingsState settings,
        string statusText,
        string errorText,
        SmartHomeSnapshot? preserveCatalog)
    {
        var zones = preserveCatalog?.Zones ?? [];
        var unassignedDevices = preserveCatalog?.UnassignedDevices ?? [];
        var flows = preserveCatalog?.Flows ?? [];
        var moods = preserveCatalog?.Moods ?? [];
        var overview = BuildOverview(zones, unassignedDevices, flows, moods);

        return new SmartHomeSnapshot(
            settings,
            overview,
            statusText,
            errorText,
            preserveCatalog?.RefreshedAtUtc,
            zones,
            unassignedDevices,
            flows,
            moods);
    }

    private static SmartHomeSettingsState BuildSettingsState(
        SmartHomeConfiguration configuration,
        bool isConnected,
        string statusText)
    {
        var providerId = SmartHomeSettingsStore.NormalizeProviderId(configuration.ProviderId);
        return new SmartHomeSettingsState(
            providerId,
            [
                new SmartHomeProviderOptionState(
                    SmartHomeProviderIds.Homey,
                    "Homey",
                    "Rooms, devices, scenes, and flows from the Homey Web API.",
                    Supported: true,
                    Selected: string.Equals(providerId, SmartHomeProviderIds.Homey, StringComparison.Ordinal)),
                new SmartHomeProviderOptionState(
                    SmartHomeProviderIds.HomeAssistant,
                    "Home Assistant",
                    "Planned next. The shared room/device UI is being built to support it.",
                    Supported: false,
                    Selected: string.Equals(providerId, SmartHomeProviderIds.HomeAssistant, StringComparison.Ordinal))
            ],
            new SmartHomeHomeySettingsState(
                configuration.Homey.BaseUrl,
                configuration.Homey.HomeyId,
                !string.IsNullOrWhiteSpace(configuration.Homey.SessionToken),
                !string.IsNullOrWhiteSpace(configuration.Homey.BaseUrl) &&
                !string.IsNullOrWhiteSpace(configuration.Homey.SessionToken),
                isConnected,
                statusText));
    }

    private static string BuildConnectionStatusText(
        SmartHomeHomeyConfiguration configuration,
        bool isConnected,
        string errorText)
    {
        if (isConnected)
        {
            return "Connected and ready.";
        }

        if (string.IsNullOrWhiteSpace(configuration.BaseUrl) &&
            string.IsNullOrWhiteSpace(configuration.SessionToken))
        {
            return "Waiting for a Homey address and session token.";
        }

        if (string.IsNullOrWhiteSpace(configuration.BaseUrl))
        {
            return "Add the Homey address or local IP.";
        }

        if (string.IsNullOrWhiteSpace(configuration.SessionToken))
        {
            return "Add a Homey session token to authenticate.";
        }

        return string.IsNullOrWhiteSpace(errorText)
            ? "Configured. Refresh to test the connection."
            : errorText;
    }

    private static SmartHomeOverviewState BuildOverview(
        IReadOnlyList<SmartHomeZoneState> zones,
        IReadOnlyList<SmartHomeDeviceState> unassignedDevices,
        IReadOnlyList<SmartHomeFlowState> flows,
        IReadOnlyList<SmartHomeMoodState> moods)
    {
        var allDevices = zones.SelectMany(zone => zone.Devices).Concat(unassignedDevices).ToArray();
        return new SmartHomeOverviewState(
            zones.Count,
            allDevices.Length,
            allDevices.Count(device => device.Controls.Count > 0),
            allDevices.Count(device => device.Available),
            allDevices.Count(device => device.IsLight),
            flows.Count,
            flows.Count(flow => flow.IsAdvanced),
            moods.Count);
    }

    private static SmartHomeDeviceState MapDevice(
        HomeyDeviceData device,
        IReadOnlyDictionary<string, HomeyZoneData> zones)
    {
        var controls = BuildControls(device);
        var swatch = BuildDeviceSwatch(device, controls);
        var zoneName = zones.TryGetValue(device.ZoneId, out var zone)
            ? zone.Name
            : string.Empty;
        var isOn = controls.FirstOrDefault(control =>
            string.Equals(control.CapabilityId, "onoff", StringComparison.OrdinalIgnoreCase))?.BooleanValue ?? false;
        var isLight =
            string.Equals(device.DeviceClass, "light", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(device.VirtualClass, "light", StringComparison.OrdinalIgnoreCase);
        var supportsColor = controls.Any(control =>
            string.Equals(control.CapabilityId, "light_hue", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(control.CapabilityId, "light_saturation", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(control.CapabilityId, "light_temperature", StringComparison.OrdinalIgnoreCase));

        return new SmartHomeDeviceState(
            device.Id,
            device.Name,
            string.IsNullOrWhiteSpace(device.DeviceClass) ? "device" : device.DeviceClass,
            device.ZoneId,
            zoneName,
            device.Available,
            isOn,
            isLight,
            supportsColor,
            BuildDeviceStatusText(device, controls),
            swatch.Hex,
            swatch.Label,
            device.IconUrl,
            device.DriverId,
            device.DriverUri,
            controls,
            BuildDeviceTags(device, controls, isLight, supportsColor));
    }

    private static IReadOnlyList<SmartHomeControlState> BuildControls(HomeyDeviceData device)
    {
        var controls = new List<SmartHomeControlState>();
        foreach (var capabilityId in device.CapabilityOrder)
        {
            if (!device.Capabilities.TryGetValue(capabilityId, out var capability))
            {
                continue;
            }

            var control = capabilityId.ToLowerInvariant() switch
            {
                "onoff" => CreateSwitchControl(capability),
                "dim" => CreatePercentageSliderControl(capability, "Brightness", "Adjust the light level.", SmartHomeControlAccents.Brightness),
                "light_hue" => CreateHueSliderControl(capability),
                "light_saturation" => CreatePercentageSliderControl(capability, "Saturation", "Adjust the color intensity.", SmartHomeControlAccents.Saturation),
                "light_temperature" => CreatePercentageSliderControl(capability, "Temperature", "Shift between warm and cool white.", SmartHomeControlAccents.Temperature),
                _ => null
            };

            if (control is not null)
            {
                controls.Add(control);
            }
        }

        return controls
            .OrderBy(control => Array.IndexOf(CapabilityControlOrder, control.CapabilityId))
            .ThenBy(control => control.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SmartHomeControlState? CreateSwitchControl(HomeyCapabilityData capability)
    {
        if (!capability.Setable || capability.BooleanValue is not bool value)
        {
            return null;
        }

        return new SmartHomeControlState(
            $"capability:{capability.Id}",
            capability.Id,
            SmartHomeControlKinds.Switch,
            "Power",
            "Turn this device on or off.",
            Enabled: true,
            BooleanValue: value,
            NumericValue: value ? 1 : 0,
            Min: 0,
            Max: 1,
            Step: 1,
            ValueLabel: value ? "On" : "Off",
            Accent: SmartHomeControlAccents.Neutral,
            PreviewHex: string.Empty);
    }

    private static SmartHomeControlState? CreateHueSliderControl(HomeyCapabilityData capability)
    {
        if (!capability.Setable || capability.NumberValue is not double value)
        {
            return null;
        }

        var degrees = Clamp(Math.Round(value * 360d), 0, 360);
        return new SmartHomeControlState(
            $"capability:{capability.Id}",
            capability.Id,
            SmartHomeControlKinds.Slider,
            "Hue",
            "Slide through the active lamp color.",
            Enabled: true,
            BooleanValue: false,
            NumericValue: degrees,
            Min: 0,
            Max: 360,
            Step: 5,
            ValueLabel: $"{degrees:0} deg",
            Accent: SmartHomeControlAccents.Hue,
            PreviewHex: SmartHomeColorHelper.HsvToHex(value, 1d, 1d));
    }

    private static SmartHomeControlState? CreatePercentageSliderControl(
        HomeyCapabilityData capability,
        string title,
        string copy,
        string accent)
    {
        if (!capability.Setable || capability.NumberValue is not double value)
        {
            return null;
        }

        var percent = Clamp(Math.Round(value * 100d), 0, 100);
        return new SmartHomeControlState(
            $"capability:{capability.Id}",
            capability.Id,
            SmartHomeControlKinds.Slider,
            title,
            copy,
            Enabled: true,
            BooleanValue: false,
            NumericValue: percent,
            Min: 0,
            Max: 100,
            Step: capability.Step is double step && step > 0 ? Math.Max(1d, Math.Round(step * 100d)) : 5d,
            ValueLabel: $"{percent:0}%",
            Accent: accent,
            PreviewHex: string.Empty);
    }

    private static string BuildDeviceStatusText(
        HomeyDeviceData device,
        IReadOnlyList<SmartHomeControlState> controls)
    {
        if (!device.Available)
        {
            if (!string.IsNullOrWhiteSpace(device.UnavailableMessage))
            {
                return device.UnavailableMessage;
            }

            if (!string.IsNullOrWhiteSpace(device.WarningMessage))
            {
                return device.WarningMessage;
            }

            return "Unavailable";
        }

        var parts = new List<string>();
        var power = controls.FirstOrDefault(control =>
            string.Equals(control.CapabilityId, "onoff", StringComparison.OrdinalIgnoreCase));
        var brightness = controls.FirstOrDefault(control =>
            string.Equals(control.CapabilityId, "dim", StringComparison.OrdinalIgnoreCase));
        if (power is not null)
        {
            parts.Add(power.BooleanValue ? "On" : "Off");
        }

        if (brightness is not null)
        {
            parts.Add($"{brightness.NumericValue:0}%");
        }

        if (controls.Any(control =>
                string.Equals(control.CapabilityId, "light_hue", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(control.CapabilityId, "light_temperature", StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add("Color ready");
        }

        if (!parts.Count.Equals(0))
        {
            return string.Join(" - ", parts);
        }

        return !string.IsNullOrWhiteSpace(device.WarningMessage)
            ? device.WarningMessage
            : "Ready";
    }

    private static IReadOnlyList<string> BuildDeviceTags(
        HomeyDeviceData device,
        IReadOnlyList<SmartHomeControlState> controls,
        bool isLight,
        bool supportsColor)
    {
        var tags = new List<string>();
        if (isLight)
        {
            tags.Add("Light");
        }

        if (controls.Any(control => string.Equals(control.CapabilityId, "dim", StringComparison.OrdinalIgnoreCase)))
        {
            tags.Add("Dimmer");
        }

        if (supportsColor)
        {
            tags.Add("Color");
        }

        if (!device.Available)
        {
            tags.Add("Unavailable");
        }

        if (!tags.Count.Equals(0))
        {
            return tags;
        }

        return !string.IsNullOrWhiteSpace(device.DeviceClass)
            ? [device.DeviceClass]
            : [];
    }

    private static SmartHomeFlowState MapFlow(HomeyFlowData flow)
    {
        var triggerable = (flow.Triggerable ?? true) && flow.Enabled && !flow.Broken;
        var kindText = flow.IsAdvanced ? "Advanced Flow" : "Flow";
        var detailText = flow.Broken
            ? $"{kindText} has a broken card."
            : flow.Enabled
                ? $"Trigger this {kindText.ToLowerInvariant()} from Quick Access."
                : $"{kindText} is disabled in Homey.";

        return new SmartHomeFlowState(
            flow.Id,
            flow.Name,
            flow.IsAdvanced,
            flow.Enabled,
            flow.Broken,
            triggerable,
            flow.FolderId,
            detailText,
            flow.IsAdvanced ? "Advanced" : "Flow");
    }

    private static SmartHomeMoodState MapMood(
        HomeyMoodData mood,
        IReadOnlyDictionary<string, HomeyZoneData> zones)
    {
        var zoneName = zones.TryGetValue(mood.ZoneId, out var zone)
            ? zone.Name
            : string.Empty;
        var zonePath = !string.IsNullOrWhiteSpace(mood.ZoneId)
            ? ResolveZonePath(zones, mood.ZoneId)
            : "Unassigned";
        var deviceCount = mood.DeviceIds.Count;
        var presetText = NormalizeText(mood.Preset);
        var detailParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(zoneName))
        {
            detailParts.Add(zoneName);
        }

        detailParts.Add(deviceCount == 1 ? "1 device" : $"{deviceCount} devices");

        if (!string.IsNullOrWhiteSpace(presetText))
        {
            detailParts.Add($"Preset: {presetText}");
        }

        return new SmartHomeMoodState(
            mood.Id,
            mood.Name,
            mood.ZoneId,
            zoneName,
            zonePath,
            deviceCount,
            presetText,
            string.Join(" - ", detailParts.Where(part => !string.IsNullOrWhiteSpace(part))),
            string.IsNullOrWhiteSpace(presetText) ? "Mood" : "Preset");
    }

    private static (string Hex, string Label) BuildDeviceSwatch(
        HomeyDeviceData device,
        IReadOnlyList<SmartHomeControlState> controls)
    {
        var hue = controls.FirstOrDefault(control =>
            string.Equals(control.CapabilityId, "light_hue", StringComparison.OrdinalIgnoreCase))?.NumericValue;
        var saturation = controls.FirstOrDefault(control =>
            string.Equals(control.CapabilityId, "light_saturation", StringComparison.OrdinalIgnoreCase))?.NumericValue;
        var dim = controls.FirstOrDefault(control =>
            string.Equals(control.CapabilityId, "dim", StringComparison.OrdinalIgnoreCase))?.NumericValue;
        var temperature = controls.FirstOrDefault(control =>
            string.Equals(control.CapabilityId, "light_temperature", StringComparison.OrdinalIgnoreCase))?.NumericValue;
        var powerOn = controls.FirstOrDefault(control =>
            string.Equals(control.CapabilityId, "onoff", StringComparison.OrdinalIgnoreCase))?.BooleanValue ?? false;

        var brightness = Math.Max(powerOn ? 0.2d : 0.12d, ((dim ?? 100d) / 100d));
        if (hue is double hueDegrees)
        {
            var saturationRatio = Math.Max(0d, Math.Min(1d, (saturation ?? 100d) / 100d));
            var hex = SmartHomeColorHelper.HsvToHex(hueDegrees / 360d, saturationRatio, brightness);
            var label = saturationRatio < 0.08d
                ? "White tone"
                : $"Hue {Math.Round(hueDegrees):0} deg";
            return (hex, label);
        }

        if (temperature is double temperaturePercent)
        {
            var temperatureRatio = Math.Max(0d, Math.Min(1d, temperaturePercent / 100d));
            var hex = SmartHomeColorHelper.TemperatureToHex(temperatureRatio, brightness);
            var label = temperatureRatio < 0.34d
                ? "Warm white"
                : temperatureRatio > 0.66d
                    ? "Cool white"
                    : "Neutral white";
            return (hex, label);
        }

        return (string.Empty, string.Empty);
    }

    private static string ResolveZonePath(IReadOnlyDictionary<string, HomeyZoneData> zones, string zoneId)
    {
        if (!zones.TryGetValue(zoneId, out var zone))
        {
            return "Unassigned";
        }

        var ancestors = new Stack<string>();
        var current = zone;
        ancestors.Push(string.IsNullOrWhiteSpace(current.Name) ? current.Id : current.Name);

        while (!string.IsNullOrWhiteSpace(current.ParentId) && zones.TryGetValue(current.ParentId, out var parent))
        {
            ancestors.Push(string.IsNullOrWhiteSpace(parent.Name) ? parent.Id : parent.Name);
            current = parent;
        }

        return string.Join(" / ", ancestors);
    }

    private static int ResolveZoneDepth(IReadOnlyDictionary<string, HomeyZoneData> zones, string zoneId)
    {
        var depth = 0;
        if (!zones.TryGetValue(zoneId, out var zone))
        {
            return depth;
        }

        while (!string.IsNullOrWhiteSpace(zone.ParentId) && zones.TryGetValue(zone.ParentId, out var parent))
        {
            depth += 1;
            zone = parent;
        }

        return depth;
    }

    private static void EnsureHomeyConfigured(SmartHomeConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.Homey.BaseUrl))
        {
            throw new InvalidOperationException("Save the Homey address before sending commands.");
        }

        if (string.IsNullOrWhiteSpace(configuration.Homey.SessionToken))
        {
            throw new InvalidOperationException("Save a Homey session token before sending commands.");
        }
    }

    private static SmartHomeSnapshot PatchCapabilityValue(
        SmartHomeSnapshot snapshot,
        string deviceId,
        string capabilityId,
        JsonElement value)
    {
        var zones = snapshot.Zones
            .Select(zone =>
            {
                var devices = zone.Devices.Select(device => PatchDevice(device, deviceId, capabilityId, value)).ToArray();
                return zone with
                {
                    Devices = devices,
                    LightCount = devices.Count(device => device.IsLight),
                    DeviceCount = devices.Length
                };
            })
            .ToArray();

        var unassignedDevices = snapshot.UnassignedDevices
            .Select(device => PatchDevice(device, deviceId, capabilityId, value))
            .ToArray();

        return snapshot with
        {
            Zones = zones,
            UnassignedDevices = unassignedDevices,
            Overview = BuildOverview(zones, unassignedDevices, snapshot.Flows, snapshot.Moods),
            RefreshedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static SmartHomeDeviceState PatchDevice(
        SmartHomeDeviceState device,
        string deviceId,
        string capabilityId,
        JsonElement value)
    {
        if (!string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            return device;
        }

        var controls = device.Controls
            .Select(control => PatchControl(control, capabilityId, value))
            .ToArray();

        var swatch = BuildDeviceSwatchFromControls(device, controls);
        var isOn = controls.FirstOrDefault(control =>
            string.Equals(control.CapabilityId, "onoff", StringComparison.OrdinalIgnoreCase))?.BooleanValue ?? device.IsOn;
        var statusText = BuildDeviceStatusTextFromControls(device, controls);

        return device with
        {
            Controls = controls,
            IsOn = isOn,
            StatusText = statusText,
            SwatchHex = swatch.Hex,
            SwatchLabel = swatch.Label
        };
    }

    private static SmartHomeControlState PatchControl(
        SmartHomeControlState control,
        string capabilityId,
        JsonElement value)
    {
        if (!string.Equals(control.CapabilityId, capabilityId, StringComparison.OrdinalIgnoreCase))
        {
            return control;
        }

        if (string.Equals(control.Kind, SmartHomeControlKinds.Switch, StringComparison.OrdinalIgnoreCase) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            var boolValue = value.GetBoolean();
            return control with
            {
                BooleanValue = boolValue,
                NumericValue = boolValue ? 1 : 0,
                ValueLabel = boolValue ? "On" : "Off"
            };
        }

        if (string.Equals(control.Kind, SmartHomeControlKinds.Slider, StringComparison.OrdinalIgnoreCase) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var rawValue))
        {
            var numericValue = control.Accent switch
            {
                SmartHomeControlAccents.Hue => Clamp(Math.Round(rawValue * 360d), control.Min, control.Max),
                _ => Clamp(Math.Round(rawValue * 100d), control.Min, control.Max)
            };

            var valueLabel = control.Accent == SmartHomeControlAccents.Hue
                ? $"{numericValue:0} deg"
                : $"{numericValue:0}%";

            var previewHex = control.Accent == SmartHomeControlAccents.Hue
                ? SmartHomeColorHelper.HsvToHex(rawValue, 1d, 1d)
                : control.PreviewHex;

            return control with
            {
                NumericValue = numericValue,
                ValueLabel = valueLabel,
                PreviewHex = previewHex
            };
        }

        return control;
    }

    private static (string Hex, string Label) BuildDeviceSwatchFromControls(
        SmartHomeDeviceState device,
        IReadOnlyList<SmartHomeControlState> controls)
    {
        var hue = controls.FirstOrDefault(control =>
            string.Equals(control.CapabilityId, "light_hue", StringComparison.OrdinalIgnoreCase))?.NumericValue;
        var saturation = controls.FirstOrDefault(control =>
            string.Equals(control.CapabilityId, "light_saturation", StringComparison.OrdinalIgnoreCase))?.NumericValue;
        var dim = controls.FirstOrDefault(control =>
            string.Equals(control.CapabilityId, "dim", StringComparison.OrdinalIgnoreCase))?.NumericValue;
        var temperature = controls.FirstOrDefault(control =>
            string.Equals(control.CapabilityId, "light_temperature", StringComparison.OrdinalIgnoreCase))?.NumericValue;
        var powerOn = controls.FirstOrDefault(control =>
            string.Equals(control.CapabilityId, "onoff", StringComparison.OrdinalIgnoreCase))?.BooleanValue ?? device.IsOn;

        var brightness = Math.Max(powerOn ? 0.2d : 0.12d, ((dim ?? 100d) / 100d));
        if (hue is double hueDegrees)
        {
            var saturationRatio = Math.Max(0d, Math.Min(1d, (saturation ?? 100d) / 100d));
            var hex = SmartHomeColorHelper.HsvToHex(hueDegrees / 360d, saturationRatio, brightness);
            var label = saturationRatio < 0.08d ? "White tone" : $"Hue {Math.Round(hueDegrees):0} deg";
            return (hex, label);
        }

        if (temperature is double temperaturePercent)
        {
            var temperatureRatio = Math.Max(0d, Math.Min(1d, temperaturePercent / 100d));
            var hex = SmartHomeColorHelper.TemperatureToHex(temperatureRatio, brightness);
            var label = temperatureRatio < 0.34d
                ? "Warm white"
                : temperatureRatio > 0.66d
                    ? "Cool white"
                    : "Neutral white";
            return (hex, label);
        }

        return (string.Empty, string.Empty);
    }

    private static string BuildDeviceStatusTextFromControls(
        SmartHomeDeviceState device,
        IReadOnlyList<SmartHomeControlState> controls)
    {
        if (!device.Available)
        {
            return string.IsNullOrWhiteSpace(device.StatusText)
                ? "Unavailable"
                : device.StatusText;
        }

        var parts = new List<string>();
        var power = controls.FirstOrDefault(control =>
            string.Equals(control.CapabilityId, "onoff", StringComparison.OrdinalIgnoreCase));
        var brightness = controls.FirstOrDefault(control =>
            string.Equals(control.CapabilityId, "dim", StringComparison.OrdinalIgnoreCase));

        if (power is not null)
        {
            parts.Add(power.BooleanValue ? "On" : "Off");
        }

        if (brightness is not null)
        {
            parts.Add($"{brightness.NumericValue:0}%");
        }

        if (controls.Any(control =>
                string.Equals(control.CapabilityId, "light_hue", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(control.CapabilityId, "light_temperature", StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add("Color ready");
        }

        return parts.Count > 0 ? string.Join(" - ", parts) : device.StatusText;
    }

    private void InvalidateSnapshot()
    {
        _cachedSnapshotAtUtc = DateTimeOffset.MinValue;
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Max(min, Math.Min(max, value));
    }

    private static string NormalizeText(string? value)
    {
        return (value ?? string.Empty).Trim();
    }
}

internal static class SmartHomeColorHelper
{
    public static string HsvToHex(double hueRatio, double saturation, double brightness)
    {
        var h = NormalizeHue(hueRatio) * 6d;
        var s = Clamp(saturation, 0d, 1d);
        var v = Clamp(brightness, 0d, 1d);

        var sector = (int)Math.Floor(h) % 6;
        var fraction = h - Math.Floor(h);
        var p = v * (1d - s);
        var q = v * (1d - fraction * s);
        var t = v * (1d - (1d - fraction) * s);

        var (r, g, b) = sector switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q)
        };

        return ToHex(r, g, b);
    }

    public static string TemperatureToHex(double temperatureRatio, double brightness)
    {
        var ratio = Clamp(temperatureRatio, 0d, 1d);
        var value = Clamp(brightness, 0d, 1d);

        var warm = (R: 255d / 255d, G: 171d / 255d, B: 93d / 255d);
        var cool = (R: 164d / 255d, G: 214d / 255d, B: 255d / 255d);

        var r = Lerp(warm.R, cool.R, ratio) * value;
        var g = Lerp(warm.G, cool.G, ratio) * value;
        var b = Lerp(warm.B, cool.B, ratio) * value;

        return ToHex(r, g, b);
    }

    private static double NormalizeHue(double hueRatio)
    {
        var normalized = hueRatio % 1d;
        return normalized < 0 ? normalized + 1d : normalized;
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Max(min, Math.Min(max, value));
    }

    private static double Lerp(double start, double end, double amount)
    {
        return start + ((end - start) * amount);
    }

    private static string ToHex(double r, double g, double b)
    {
        return $"#{ToByte(r):X2}{ToByte(g):X2}{ToByte(b):X2}";
    }

    private static byte ToByte(double value)
    {
        return (byte)Math.Max(0, Math.Min(255, Math.Round(value * 255d)));
    }
}
