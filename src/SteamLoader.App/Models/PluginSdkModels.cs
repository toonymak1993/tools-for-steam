using System.Text.Json;

namespace SteamLoader.App.Models;

public sealed record PluginSdkState(
    string PluginId,
    string SdkVersion,
    string EntryPoint,
    IReadOnlyList<string> Permissions,
    JsonElement Settings,
    IReadOnlyDictionary<string, bool> Secrets);

public sealed record PluginSdkSettingsState(
    JsonElement Settings);

public sealed record PluginSdkSecretsState(
    IReadOnlyDictionary<string, bool> Secrets);

public sealed record PluginSdkNetworkRequest(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string>? Headers,
    JsonElement? Body,
    string? AuthorizationSecretKey,
    string? AuthorizationScheme);

public sealed record PluginSdkNetworkResponse(
    int StatusCode,
    bool Ok,
    string ContentType,
    string BodyText,
    IReadOnlyDictionary<string, string> Headers);
