using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamLoader.App.Infrastructure.SmartHome;

internal sealed class HomeySmartHomeClient
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public HomeySmartHomeClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<HomeyCatalogData> GetCatalogAsync(
        SmartHomeHomeyConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveBaseUri(configuration.BaseUrl);
        var sessionToken = configuration.SessionToken;

        var zonesTask = GetZonesAsync(baseUri, sessionToken, cancellationToken);
        var devicesTask = GetDevicesAsync(baseUri, sessionToken, cancellationToken);
        var flowsTask = GetFlowsAsync(baseUri, sessionToken, isAdvanced: false, cancellationToken);
        var advancedFlowsTask = GetFlowsAsync(baseUri, sessionToken, isAdvanced: true, cancellationToken);
        var moodsTask = GetMoodsAsync(baseUri, sessionToken, cancellationToken);

        await Task.WhenAll(zonesTask, devicesTask, flowsTask, advancedFlowsTask, moodsTask);

        return new HomeyCatalogData(
            await zonesTask,
            await devicesTask,
            await flowsTask,
            await advancedFlowsTask,
            await moodsTask);
    }

    public async Task SetCapabilityValueAsync(
        SmartHomeHomeyConfiguration configuration,
        string deviceId,
        string capabilityId,
        JsonElement value,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveBaseUri(configuration.BaseUrl);
        var requestUri = new Uri(
            baseUri,
            $"api/manager/devices/device/{Uri.EscapeDataString(deviceId)}/capability/{Uri.EscapeDataString(capabilityId)}");
        using var request = CreateRequest(HttpMethod.Put, requestUri, configuration.SessionToken);
        var payload = new CapabilityValueRequest(value, CreateTransactionId());
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, RequestJsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task TriggerFlowAsync(
        SmartHomeHomeyConfiguration configuration,
        string flowId,
        bool isAdvanced,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveBaseUri(configuration.BaseUrl);
        var path = isAdvanced
            ? $"api/manager/flow/advancedflow/{Uri.EscapeDataString(flowId)}/trigger"
            : $"api/manager/flow/flow/{Uri.EscapeDataString(flowId)}/trigger";
        using var request = CreateRequest(HttpMethod.Post, new Uri(baseUri, path), configuration.SessionToken);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SetMoodAsync(
        SmartHomeHomeyConfiguration configuration,
        string moodId,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveBaseUri(configuration.BaseUrl);
        var path = $"api/manager/moods/mood/{Uri.EscapeDataString(moodId)}/set";
        using var request = CreateRequest(HttpMethod.Post, new Uri(baseUri, path), configuration.SessionToken);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, HomeyZoneData>> GetZonesAsync(
        Uri baseUri,
        string sessionToken,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonDocumentAsync(baseUri, sessionToken, "api/manager/zones/zone", cancellationToken);
        var zones = new Dictionary<string, HomeyZoneData>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            zones[property.Name] = ParseZone(property.Name, property.Value);
        }

        return zones;
    }

    private async Task<IReadOnlyDictionary<string, HomeyDeviceData>> GetDevicesAsync(
        Uri baseUri,
        string sessionToken,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonDocumentAsync(baseUri, sessionToken, "api/manager/devices/device", cancellationToken);
        var devices = new Dictionary<string, HomeyDeviceData>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            devices[property.Name] = ParseDevice(baseUri, property.Name, property.Value);
        }

        return devices;
    }

    private async Task<IReadOnlyDictionary<string, HomeyFlowData>> GetFlowsAsync(
        Uri baseUri,
        string sessionToken,
        bool isAdvanced,
        CancellationToken cancellationToken)
    {
        var path = isAdvanced
            ? "api/manager/flow/advancedflow"
            : "api/manager/flow/flow";
        using var document = await GetJsonDocumentAsync(baseUri, sessionToken, path, cancellationToken);
        var flows = new Dictionary<string, HomeyFlowData>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            flows[property.Name] = ParseFlow(property.Name, property.Value, isAdvanced);
        }

        return flows;
    }

    private async Task<IReadOnlyDictionary<string, HomeyMoodData>> GetMoodsAsync(
        Uri baseUri,
        string sessionToken,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonDocumentAsync(baseUri, sessionToken, "api/manager/moods/mood", cancellationToken);
        var moods = new Dictionary<string, HomeyMoodData>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            moods[property.Name] = ParseMood(property.Name, property.Value);
        }

        return moods;
    }

    private async Task<JsonDocument> GetJsonDocumentAsync(
        Uri baseUri,
        string sessionToken,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri(baseUri, path), sessionToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string sessionToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(sessionToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
        }

        return request;
    }

    private static Uri ResolveBaseUri(string baseUrl)
    {
        var normalized = SmartHomeSettingsStore.NormalizeBaseUrl(baseUrl);
        if (!Uri.TryCreate($"{normalized.TrimEnd('/')}/", UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("The Homey base URL is invalid.");
        }

        return baseUri;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Homey rejected the session token.",
            HttpStatusCode.Forbidden => "Homey denied access to this API session.",
            HttpStatusCode.NotFound => "The Homey API endpoint was not found. Check the address and whether the device is reachable.",
            _ => $"Homey request failed with {(int)response.StatusCode} {response.ReasonPhrase}."
        };

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(responseText))
        {
            if (TryExtractErrorMessage(responseText, out var extractedMessage))
            {
                message = extractedMessage;
            }
            else
            {
                message = $"{message} {responseText.Trim()}".Trim();
            }
        }

        throw new InvalidOperationException(message);
    }

    private static string CreateTransactionId()
    {
        return $"homey-api-{Guid.NewGuid():N}";
    }

    private static bool TryExtractErrorMessage(string responseText, out string message)
    {
        try
        {
            using var errorDocument = JsonDocument.Parse(responseText);
            return TryExtractErrorMessage(errorDocument.RootElement, out message);
        }
        catch
        {
            message = string.Empty;
            return false;
        }
    }

    private static bool TryExtractErrorMessage(JsonElement element, out string message)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                message = text;
                return true;
            }
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "message", "error_description", "description", "error" })
            {
                if (element.TryGetProperty(propertyName, out var property) &&
                    TryExtractErrorMessage(property, out message))
                {
                    return true;
                }
            }
        }

        message = string.Empty;
        return false;
    }

    private static HomeyZoneData ParseZone(string zoneId, JsonElement element)
    {
        return new HomeyZoneData(
            zoneId,
            GetString(element, "name", zoneId),
            GetString(element, "parent"),
            GetString(element, "icon"));
    }

    private static HomeyDeviceData ParseDevice(Uri baseUri, string deviceId, JsonElement element)
    {
        var capabilities = new Dictionary<string, HomeyCapabilityData>(StringComparer.OrdinalIgnoreCase);
        if (TryGetProperty(element, "capabilitiesObj", out var capabilitiesElement) &&
            capabilitiesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in capabilitiesElement.EnumerateObject())
            {
                capabilities[property.Name] = ParseCapability(property.Name, property.Value);
            }
        }

        return new HomeyDeviceData(
            deviceId,
            GetString(element, "name", deviceId),
            ResolveDeviceClass(element),
            GetString(element, "virtualClass"),
            GetString(element, "zone"),
            GetString(element, "driverId"),
            GetString(element, "driverUri"),
            GetString(element, "warningMessage"),
            GetString(element, "unavailableMessage"),
            GetBool(element, "available", GetBool(element, "ready", true)),
            ResolveDeviceIconUrl(baseUri, element),
            GetStringList(element, "capabilities"),
            capabilities);
    }

    private static HomeyCapabilityData ParseCapability(string capabilityId, JsonElement element)
    {
        var type = GetString(element, "type");
        var boolValue = TryGetBoolean(element, "value");
        var numberValue = TryGetDouble(element, "value");
        var stringValue = TryGetString(element, "value");

        return new HomeyCapabilityData(
            capabilityId,
            GetString(element, "title", capabilityId),
            GetString(element, "desc"),
            GetString(element, "units"),
            type,
            GetBool(element, "setable"),
            GetBool(element, "getable", true),
            boolValue,
            numberValue,
            stringValue,
            TryGetDouble(element, "min"),
            TryGetDouble(element, "max"),
            TryGetDouble(element, "step"));
    }

    private static HomeyFlowData ParseFlow(string flowId, JsonElement element, bool isAdvanced)
    {
        return new HomeyFlowData(
            flowId,
            GetString(element, "name", flowId),
            isAdvanced,
            GetBool(element, "enabled", true),
            GetBool(element, "broken"),
            TryGetBoolean(element, "triggerable"),
            GetString(element, "folder"));
    }

    private static HomeyMoodData ParseMood(string moodId, JsonElement element)
    {
        return new HomeyMoodData(
            moodId,
            GetString(element, "name", moodId),
            GetString(element, "preset"),
            GetString(element, "zone"),
            GetObjectKeys(element, "devices"));
    }

    private static string ResolveDeviceClass(JsonElement element)
    {
        var virtualClass = GetString(element, "virtualClass");
        if (!string.IsNullOrWhiteSpace(virtualClass))
        {
            return virtualClass;
        }

        return GetString(element, "class");
    }

    private static string ResolveDeviceIconUrl(Uri baseUri, JsonElement element)
    {
        if (TryGetProperty(element, "iconObj", out var iconObjElement) &&
            iconObjElement.ValueKind == JsonValueKind.Object &&
            iconObjElement.TryGetProperty("url", out var iconUrlElement) &&
            iconUrlElement.ValueKind == JsonValueKind.String)
        {
            return ResolveAbsoluteUrl(baseUri, iconUrlElement.GetString());
        }

        return ResolveAbsoluteUrl(baseUri, GetString(element, "icon"));
    }

    private static string ResolveAbsoluteUrl(Uri baseUri, string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return Uri.TryCreate(raw, UriKind.Absolute, out var absoluteUri)
            ? absoluteUri.ToString()
            : new Uri(baseUri, raw.TrimStart('/')).ToString();
    }

    private static IReadOnlyList<string> GetStringList(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static IReadOnlyList<string> GetObjectKeys(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return property.EnumerateObject()
            .Select(entry => entry.Name)
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .ToArray();
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out property))
        {
            return true;
        }

        property = default;
        return false;
    }

    private static string GetString(JsonElement element, string propertyName, string fallback = "")
    {
        return TryGetString(element, propertyName) ?? fallback;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool GetBool(JsonElement element, string propertyName, bool fallback = false)
    {
        return TryGetBoolean(element, propertyName) ?? fallback;
    }

    private static bool? TryGetBoolean(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
        {
            return value;
        }

        return null;
    }

    private sealed record CapabilityValueRequest(
        [property: JsonPropertyName("value")] JsonElement Value,
        [property: JsonPropertyName("transactionId")] string TransactionId);
}

internal sealed record HomeyCatalogData(
    IReadOnlyDictionary<string, HomeyZoneData> Zones,
    IReadOnlyDictionary<string, HomeyDeviceData> Devices,
    IReadOnlyDictionary<string, HomeyFlowData> Flows,
    IReadOnlyDictionary<string, HomeyFlowData> AdvancedFlows,
    IReadOnlyDictionary<string, HomeyMoodData> Moods);

internal sealed record HomeyZoneData(
    string Id,
    string Name,
    string ParentId,
    string Icon);

internal sealed record HomeyDeviceData(
    string Id,
    string Name,
    string DeviceClass,
    string VirtualClass,
    string ZoneId,
    string DriverId,
    string DriverUri,
    string WarningMessage,
    string UnavailableMessage,
    bool Available,
    string IconUrl,
    IReadOnlyList<string> CapabilityOrder,
    IReadOnlyDictionary<string, HomeyCapabilityData> Capabilities);

internal sealed record HomeyCapabilityData(
    string Id,
    string Title,
    string Description,
    string Units,
    string Type,
    bool Setable,
    bool Getable,
    bool? BooleanValue,
    double? NumberValue,
    string? StringValue,
    double? Min,
    double? Max,
    double? Step);

internal sealed record HomeyFlowData(
    string Id,
    string Name,
    bool IsAdvanced,
    bool Enabled,
    bool Broken,
    bool? Triggerable,
    string FolderId);

internal sealed record HomeyMoodData(
    string Id,
    string Name,
    string Preset,
    string ZoneId,
    IReadOnlyList<string> DeviceIds);
