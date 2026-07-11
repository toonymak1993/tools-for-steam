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

public sealed record PluginSdkFileListRequest(
    string? Path,
    bool Recursive);

public sealed record PluginSdkFileReadRequest(
    string Path,
    string? Encoding);

public sealed record PluginSdkFileWriteRequest(
    string Path,
    string Content,
    string? Encoding,
    bool Append,
    bool Overwrite);

public sealed record PluginSdkFilePathRequest(
    string Path,
    bool Recursive);

public sealed record PluginSdkFileTransferRequest(
    string SourcePath,
    string DestinationPath,
    bool Overwrite);

public sealed record PluginSdkFileEntry(
    string Path,
    string Name,
    bool IsDirectory,
    long Size,
    DateTimeOffset ModifiedUtc);

public sealed record PluginSdkFileListState(
    string Path,
    IReadOnlyList<PluginSdkFileEntry> Entries,
    long UsedBytes,
    long MaxBytes);

public sealed record PluginSdkFileContentState(
    string Path,
    string Content,
    string Encoding,
    long Size,
    DateTimeOffset ModifiedUtc);

public sealed record PluginSdkFileMutationState(
    string Path,
    bool Exists,
    bool IsDirectory,
    long Size,
    long UsedBytes,
    long MaxBytes);
