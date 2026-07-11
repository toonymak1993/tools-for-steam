using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.PluginStore;

public sealed class PluginStoreService
{
    private const string ManifestFileName = "tfs-plugin.json";
    private const string PermissionStorage = "storage";
    private const string PermissionSecrets = "secrets";
    private const string PermissionNetwork = "network";
    private const string PermissionFiles = "files";
    private const int SupportedSdkMajorVersion = 1;
    private const int MaxPluginSettingsBytes = 256 * 1024;
    private const int MaxPluginSecretLength = 16 * 1024;
    private const int MaxPluginNetworkRequestBytes = 512 * 1024;
    private const int MaxPluginNetworkResponseBytes = 1024 * 1024;
    private const int MaxPluginFileBytes = 8 * 1024 * 1024;
    private const long MaxPluginFilesBytes = 32L * 1024 * 1024;
    private const int MaxPluginFileEntries = 1024;
    private const int MaxCommunityCatalogBytes = 1024 * 1024;
    private const long MaxCommunityPackageBytes = 64L * 1024 * 1024;
    private const long MaxCommunityPackageExtractedBytes = 128L * 1024 * 1024;
    private const long MaxCommunityPackageEntryBytes = 32L * 1024 * 1024;
    private const int MaxCommunityPackageEntries = 512;
    private const string DefaultCommunityCatalogUrl =
        "https://raw.githubusercontent.com/toonymak1993/tfs-plugin-database/main/catalog.json";

    private static readonly HashSet<string> SupportedPluginPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        PermissionStorage,
        PermissionSecrets,
        PermissionNetwork,
        PermissionFiles,
        "frontend"
    };

    private static readonly IReadOnlyDictionary<string, string> BuiltInImageAccents =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["app-start"] = "#3FA7FF",
            ["artwork"] = "#F97316",
            ["audio"] = "#10B981",
            ["auto-sisr"] = "#F43F5E",
            ["display"] = "#EAB308",
            ["hltb"] = "#8B5CF6",
            ["performance"] = "#22C55E",
            ["power"] = "#FB7185",
            ["processes"] = "#14B8A6",
            ["smart-home"] = "#06B6D4",
            ["store-sync"] = "#60A5FA",
            ["themes"] = "#A855F7"
        };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;
    private readonly SteamLoaderSettingsService _settingsService;
    private readonly string _rootPath;
    private readonly string _catalogPath;
    private readonly string _catalogSourcePath;
    private readonly string _installedStatePath;
    private readonly string _communityRootPath;
    private readonly string _sdkDataRootPath;
    private readonly string _catalogImagesRootPath;
    private readonly string _builtInImagesRootPath;
    private readonly bool _enableCommunityCatalogBootstrap;
    private readonly object _gate = new();
    private readonly object _sdkFileGate = new();
    private readonly List<PluginStoreInputState> _inputQueue = [];
    private readonly SemaphoreSlim _catalogSyncSemaphore = new(1, 1);
    private readonly object _catalogBootstrapGate = new();
    private bool _overlayOpen;
    private long _inputNonce;
    private Task? _communityCatalogBootstrapTask;
    private string _communityCatalogBootstrapError = string.Empty;

    public PluginStoreService(
        HttpClient httpClient,
        SteamLoaderSettingsService settingsService,
        string rootPath,
        bool enableCommunityCatalogBootstrap = true)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _rootPath = rootPath;
        _catalogPath = Path.Combine(rootPath, "catalog.json");
        _catalogSourcePath = Path.Combine(rootPath, "catalog-source.json");
        _installedStatePath = Path.Combine(rootPath, "installed.json");
        _communityRootPath = Path.Combine(rootPath, "community");
        _sdkDataRootPath = Path.Combine(rootPath, "sdk-data");
        _catalogImagesRootPath = Path.Combine(rootPath, "images");
        _builtInImagesRootPath = Path.Combine(_catalogImagesRootPath, "built-in");
        _enableCommunityCatalogBootstrap = enableCommunityCatalogBootstrap;

        EnsureBuiltInImageCache();
    }

    public async Task<PluginStoreSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_enableCommunityCatalogBootstrap)
        {
            await EnsureCommunityCatalogAvailableAsync(cancellationToken);
        }

        var settingsSnapshot = _settingsService.GetSnapshot();
        var builtInById = settingsSnapshot.Plugins.ToDictionary(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase);
        var builtInPlugins = SteamLoaderPluginCatalog.Definitions
            .Select(plugin =>
            {
                builtInById.TryGetValue(plugin.Id, out var pluginSettings);
                var enabled = plugin.CanDisable ? pluginSettings?.Enabled ?? plugin.DefaultEnabled : true;
                var statusText = !plugin.CanDisable
                    ? "Built in and always available."
                    : enabled
                        ? "Built in and currently visible in Home."
                        : "Built in but currently hidden from Home.";

                return new PluginStorePluginState(
                    plugin.Id,
                    plugin.Title,
                    plugin.Description,
                    Source: "Tools for Steam",
                    Author: "TFS Core",
                    Category: "Built-In",
                    Version: settingsSnapshot.ProductVersion,
                    InstalledVersion: settingsSnapshot.ProductVersion,
                    SdkVersion: string.Empty,
                    EntryPoint: string.Empty,
                    Permissions: [],
                    IsBuiltIn: true,
                    IsInstalled: true,
                    IsEnabled: enabled,
                    CanToggleVisibility: plugin.CanDisable,
                    CanInstall: false,
                    CanUninstall: false,
                    HasUpdate: false,
                    StatusText: statusText,
                    Tags: ["Built-In", plugin.CanDisable ? "Hideable" : "Core"],
                    Images: BuildBuiltInImages(plugin.Id, plugin.Title));
            })
            .ToArray();

        var communityCatalog = await LoadCommunityCatalogAsync(cancellationToken);
        var installedState = LoadInstalledState();
        var communityPlugins = communityCatalog.Plugins
            .Select(plugin => BuildCommunityPluginState(plugin, installedState))
            .OrderByDescending(plugin => plugin.IsInstalled)
            .ThenBy(plugin => plugin.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var installedCommunityCount = communityPlugins.Count(plugin => plugin.IsInstalled);
        var updateCount = communityPlugins.Count(plugin => plugin.HasUpdate);

        return new PluginStoreSnapshot(
            StatusText: $"{builtInPlugins.Length} built-in plugin{(builtInPlugins.Length == 1 ? string.Empty : "s")} ready - {communityPlugins.Length} community plugin{(communityPlugins.Length == 1 ? string.Empty : "s")} listed.",
            ErrorText: string.Empty,
            CatalogTitle: communityCatalog.Title,
            CatalogDescription: communityCatalog.Description,
            CommunityCatalogAvailable: communityCatalog.Available,
            CommunityCatalogStatusText: communityCatalog.StatusText,
            BuiltInCount: builtInPlugins.Length,
            CommunityCount: communityPlugins.Length,
            InstalledCommunityCount: installedCommunityCount,
            UpdateCount: updateCount,
            BuiltInPlugins: builtInPlugins,
            CommunityPlugins: communityPlugins);
    }

    public async Task<PluginStoreSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        await SyncCommunityCatalogAsync(cancellationToken);
        return await GetSnapshotAsync(cancellationToken);
    }

    public bool TryGetBuiltInImage(string pluginId, out string imagePath, out string contentType)
    {
        imagePath = string.Empty;
        contentType = string.Empty;

        if (!TryResolveBuiltInImagePath(pluginId, out var candidatePath))
        {
            return false;
        }

        imagePath = candidatePath;
        contentType = GetImageContentType(candidatePath);
        return true;
    }

    public bool TryGetCatalogImage(string fileName, out string imagePath, out string contentType)
    {
        imagePath = string.Empty;
        contentType = string.Empty;

        var normalizedFileName = NormalizeCatalogImageFileName(fileName);
        if (normalizedFileName.Length == 0)
        {
            return false;
        }

        var candidatePath = Path.GetFullPath(Path.Combine(_catalogImagesRootPath, normalizedFileName));
        EnsureWithinPathRoot(candidatePath, _catalogImagesRootPath);
        if (!File.Exists(candidatePath))
        {
            return false;
        }

        imagePath = candidatePath;
        contentType = GetImageContentType(candidatePath);
        return true;
    }

    public PluginStoreOverlayState GetOverlayState()
    {
        lock (_gate)
        {
            return new PluginStoreOverlayState(_overlayOpen);
        }
    }

    public PluginStoreOverlayState SetOverlayOpen(bool open)
    {
        lock (_gate)
        {
            _overlayOpen = open;
            if (!open)
            {
                _inputQueue.Clear();
            }

            return new PluginStoreOverlayState(_overlayOpen);
        }
    }

    public PluginStoreInputState AddOverlayInput(string action, string source)
    {
        var normalizedAction = NormalizeInputAction(action);
        if (normalizedAction.Length == 0)
        {
            throw new InvalidOperationException("A supported store input action is required.");
        }

        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim();
        if (normalizedSource.Length > 80)
        {
            normalizedSource = normalizedSource[..80];
        }

        lock (_gate)
        {
            var input = new PluginStoreInputState(++_inputNonce, normalizedAction, normalizedSource);
            _inputQueue.Add(input);

            if (_inputQueue.Count > 48)
            {
                _inputQueue.RemoveRange(0, _inputQueue.Count - 48);
            }

            return input;
        }
    }

    public PluginStoreInputBatch GetOverlayInputs(long afterNonce)
    {
        lock (_gate)
        {
            var inputs = _inputQueue
                .Where(input => input.Nonce > afterNonce)
                .ToArray();

            return new PluginStoreInputBatch(_inputNonce, inputs);
        }
    }

    public async Task<PluginStoreSnapshot> InstallCommunityPluginAsync(string pluginId, CancellationToken cancellationToken)
    {
        var catalog = await LoadCommunityCatalogAsync(cancellationToken);
        var plugin = catalog.Plugins.FirstOrDefault(entry =>
            string.Equals(entry.Id, pluginId, StringComparison.OrdinalIgnoreCase));

        if (plugin is null)
        {
            throw new InvalidOperationException("Unknown community plugin.");
        }

        if (!plugin.HasPackageSource)
        {
            throw new InvalidOperationException("This community plugin does not publish a downloadable package yet.");
        }

        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(_communityRootPath);

        var zipPath = await ResolvePackageZipAsync(catalog, plugin, cancellationToken);
        var targetDirectory = GetCommunityPluginDirectory(plugin.Id);
        var tempDirectory = Path.Combine(_rootPath, "tmp", $"{plugin.Id}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempDirectory);

        try
        {
            ValidatePackageZip(zipPath);
            ValidatePackageHash(zipPath, plugin);
            ExtractZipSafely(zipPath, tempDirectory);

            var replacementRoot = ResolveExtractedPluginRoot(tempDirectory);
            var manifest = ValidatePluginManifest(replacementRoot, plugin);
            var backupDirectory = MoveExistingCommunityPluginToBackup(targetDirectory);
            var committed = false;
            try
            {
                MoveCommunityPluginReplacement(targetDirectory, replacementRoot);

                var installedState = LoadInstalledState();
                installedState.Plugins[plugin.Id] = new InstalledCommunityPluginData
                {
                    Version = plugin.Version,
                    ManifestVersion = manifest.Version,
                    SdkVersion = manifest.SdkVersion,
                    EntryPoint = manifest.EntryPoint,
                    Permissions = manifest.Permissions,
                    InstalledAtUtc = DateTimeOffset.UtcNow
                };

                SaveInstalledState(installedState);
                committed = true;
            }
            catch
            {
                RestoreCommunityPluginBackup(targetDirectory, backupDirectory);
                throw;
            }
            finally
            {
                if (committed && !string.IsNullOrWhiteSpace(backupDirectory))
                {
                    TryDeleteDirectory(backupDirectory);
                }
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);

            if (!string.IsNullOrWhiteSpace(plugin.PackageUrl) &&
                File.Exists(zipPath) &&
                Path.GetDirectoryName(zipPath)?.StartsWith(Path.Combine(_rootPath, "tmp"), StringComparison.OrdinalIgnoreCase) == true)
            {
                TryDeleteFile(zipPath);
            }
        }

        return await GetSnapshotAsync(cancellationToken);
    }

    public async Task<PluginStoreSnapshot> UninstallCommunityPluginAsync(string pluginId, CancellationToken cancellationToken)
    {
        var normalizedPluginId = NormalizePluginId(pluginId);
        if (normalizedPluginId.Length == 0)
        {
            throw new InvalidOperationException("A community plugin ID is required.");
        }

        var targetDirectory = GetCommunityPluginDirectory(normalizedPluginId);
        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, recursive: true);
        }

        TryDeleteDirectory(GetPluginSdkDataDirectory(normalizedPluginId));

        var installedState = LoadInstalledState();
        installedState.Plugins.Remove(normalizedPluginId);
        SaveInstalledState(installedState);

        return await GetSnapshotAsync(cancellationToken);
    }

    public Task<PluginStoreSnapshot> UpdateCommunityPluginAsync(string pluginId, CancellationToken cancellationToken)
    {
        return InstallCommunityPluginAsync(pluginId, cancellationToken);
    }

    public PluginStoreCommunityRuntimeState GetCommunityRuntimeState()
    {
        var installedState = LoadInstalledState();
        var plugins = installedState.Plugins
            .Select(entry => BuildCommunityRuntimePluginState(entry.Key, entry.Value))
            .Where(plugin => plugin is not null)
            .Cast<PluginStoreCommunityRuntimePluginState>()
            .OrderBy(plugin => plugin.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PluginStoreCommunityRuntimeState(plugins);
    }

    public bool TryGetCommunityPluginFile(
        string pluginId,
        string relativePath,
        out string filePath,
        out string contentType)
    {
        filePath = string.Empty;
        contentType = string.Empty;

        try
        {
            var context = EnsureInstalledCommunityPlugin(pluginId);
            var normalizedRelativePath = NormalizePackageRelativePath(relativePath, context.Data.EntryPoint);
            var candidatePath = ResolvePackagePath(context.DirectoryPath, normalizedRelativePath);
            if (!File.Exists(candidatePath))
            {
                return false;
            }

            filePath = candidatePath;
            contentType = GetContentType(candidatePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public PluginSdkState GetPluginSdkState(string pluginId)
    {
        var context = EnsureInstalledCommunityPlugin(pluginId);
        return new PluginSdkState(
            context.PluginId,
            context.Data.SdkVersion,
            context.Data.EntryPoint,
            context.Data.Permissions,
            LoadPluginSettingsElement(context.PluginId),
            LoadPluginSecretFlags(context.PluginId));
    }

    public PluginSdkSettingsState GetPluginSdkSettings(string pluginId)
    {
        var context = EnsurePluginPermission(pluginId, PermissionStorage);
        return new PluginSdkSettingsState(LoadPluginSettingsElement(context.PluginId));
    }

    public PluginSdkSettingsState SetPluginSdkSettings(string pluginId, JsonElement settings)
    {
        var context = EnsurePluginPermission(pluginId, PermissionStorage);
        if (settings.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidOperationException("Plugin settings must be a JSON object.");
        }

        var settingsJson = JsonSerializer.Serialize(settings, JsonOptions);
        if (Encoding.UTF8.GetByteCount(settingsJson) > MaxPluginSettingsBytes)
        {
            throw new InvalidOperationException("Plugin settings are too large.");
        }

        var settingsPath = GetPluginSettingsPath(context.PluginId);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath) ?? GetPluginSdkDataDirectory(context.PluginId));
        File.WriteAllText(settingsPath, settingsJson, Encoding.UTF8);
        return new PluginSdkSettingsState(LoadPluginSettingsElement(context.PluginId));
    }

    public PluginSdkSecretsState GetPluginSdkSecrets(string pluginId)
    {
        var context = EnsurePluginPermission(pluginId, PermissionSecrets);
        return new PluginSdkSecretsState(LoadPluginSecretFlags(context.PluginId));
    }

    public PluginSdkSecretsState SetPluginSdkSecret(string pluginId, string key, string? value)
    {
        var context = EnsurePluginPermission(pluginId, PermissionSecrets);
        var normalizedKey = NormalizeSdkKey(key, "secret key");
        var secretValue = value ?? string.Empty;
        if (secretValue.Length > MaxPluginSecretLength)
        {
            throw new InvalidOperationException("Plugin secrets are too large.");
        }

        var secretStore = LoadPluginSecretStore(context.PluginId);
        secretStore.Secrets[normalizedKey] = ProtectSecret(secretValue);
        SavePluginSecretStore(context.PluginId, secretStore);
        return new PluginSdkSecretsState(LoadPluginSecretFlags(context.PluginId));
    }

    public PluginSdkSecretsState ClearPluginSdkSecret(string pluginId, string key)
    {
        var context = EnsurePluginPermission(pluginId, PermissionSecrets);
        var normalizedKey = NormalizeSdkKey(key, "secret key");
        var secretStore = LoadPluginSecretStore(context.PluginId);
        secretStore.Secrets.Remove(normalizedKey);
        SavePluginSecretStore(context.PluginId, secretStore);
        return new PluginSdkSecretsState(LoadPluginSecretFlags(context.PluginId));
    }

    public async Task<PluginSdkNetworkResponse> SendPluginSdkNetworkRequestAsync(
        string pluginId,
        PluginSdkNetworkRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new InvalidOperationException("A network request payload is required.");
        }

        var context = EnsurePluginPermission(pluginId, PermissionNetwork);
        var method = NormalizeSdkNetworkMethod(request.Method);
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not "http" and not "https")
        {
            throw new InvalidOperationException("Plugin network requests must use an absolute HTTP or HTTPS URL.");
        }

        using var message = new HttpRequestMessage(new HttpMethod(method), uri);
        var contentType = string.Empty;
        var bodyText = BuildNetworkRequestBody(request.Body);
        if (bodyText.Length > MaxPluginNetworkRequestBytes)
        {
            throw new InvalidOperationException("Plugin network request body is too large.");
        }

        foreach (var header in request.Headers ?? new Dictionary<string, string>())
        {
            var headerName = (header.Key ?? string.Empty).Trim();
            if (headerName.Length == 0)
            {
                continue;
            }

            if (IsBlockedNetworkHeader(headerName))
            {
                throw new InvalidOperationException($"Plugin network requests cannot set the {headerName} header.");
            }

            if (string.Equals(headerName, "content-type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = (header.Value ?? string.Empty).Trim();
                continue;
            }

            message.Headers.TryAddWithoutValidation(headerName, header.Value);
        }

        if (!string.IsNullOrEmpty(bodyText))
        {
            message.Content = new StringContent(bodyText, Encoding.UTF8);
            var resolvedContentType = string.IsNullOrWhiteSpace(contentType)
                ? request.Body?.ValueKind is JsonValueKind.String ? "text/plain" : "application/json"
                : contentType;

            try
            {
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(resolvedContentType);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException("The plugin network request content type is invalid.");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.AuthorizationSecretKey))
        {
            EnsurePluginPermission(pluginId, PermissionSecrets);
            var secretKey = NormalizeSdkKey(request.AuthorizationSecretKey, "authorization secret key");
            var secretValue = LoadPluginSecretValue(context.PluginId, secretKey);
            if (string.IsNullOrEmpty(secretValue))
            {
                throw new InvalidOperationException("The requested authorization secret has not been configured.");
            }

            var scheme = NormalizeAuthorizationScheme(request.AuthorizationScheme);
            message.Headers.Authorization = new AuthenticationHeaderValue(scheme, secretValue);
        }

        using var networkResponse = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var responseBody = await ReadNetworkResponseBodyAsync(networkResponse.Content, cancellationToken);
        var headers = networkResponse.Headers
            .Concat(networkResponse.Content.Headers)
            .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => string.Join(", ", group.SelectMany(header => header.Value)),
                StringComparer.OrdinalIgnoreCase);

        return new PluginSdkNetworkResponse(
            (int)networkResponse.StatusCode,
            networkResponse.IsSuccessStatusCode,
            networkResponse.Content.Headers.ContentType?.ToString() ?? string.Empty,
            responseBody,
            headers);
    }

    public PluginSdkFileListState ListPluginSdkFiles(
        string pluginId,
        PluginSdkFileListRequest? request)
    {
        var context = EnsurePluginPermission(pluginId, PermissionFiles);
        lock (_sdkFileGate)
        {
            var rootPath = GetPluginFilesDirectory(context.PluginId);
            var normalizedPath = NormalizePluginFilePath(request?.Path, allowEmpty: true);
            var targetPath = ResolvePluginFilesPath(rootPath, normalizedPath);
            if (!Directory.Exists(targetPath))
            {
                if (File.Exists(targetPath))
                {
                    throw new InvalidOperationException("The requested plugin file path is not a directory.");
                }

                return new PluginSdkFileListState(
                    normalizedPath,
                    [],
                    GetPluginFilesUsage(rootPath),
                    MaxPluginFilesBytes);
            }

            EnsurePluginFilesTreeSafe(rootPath, targetPath);
            var entries = EnumeratePluginFileEntries(rootPath, targetPath, request?.Recursive ?? false);
            return new PluginSdkFileListState(
                normalizedPath,
                entries,
                GetPluginFilesUsage(rootPath),
                MaxPluginFilesBytes);
        }
    }

    public PluginSdkFileMutationState GetPluginSdkFileInfo(string pluginId, PluginSdkFilePathRequest? request)
    {
        var context = EnsurePluginPermission(pluginId, PermissionFiles);
        lock (_sdkFileGate)
        {
            var rootPath = GetPluginFilesDirectory(context.PluginId);
            var normalizedPath = NormalizePluginFilePath(request?.Path, allowEmpty: true);
            var targetPath = ResolvePluginFilesPath(rootPath, normalizedPath);
            EnsurePluginFilesTreeSafe(rootPath, targetPath);
            return BuildPluginFileMutationState(rootPath, targetPath, normalizedPath);
        }
    }

    public PluginSdkFileContentState ReadPluginSdkFile(string pluginId, PluginSdkFileReadRequest? request)
    {
        var context = EnsurePluginPermission(pluginId, PermissionFiles);
        lock (_sdkFileGate)
        {
            var rootPath = GetPluginFilesDirectory(context.PluginId);
            var normalizedPath = NormalizePluginFilePath(request?.Path, allowEmpty: false);
            var targetPath = ResolvePluginFilesPath(rootPath, normalizedPath);
            EnsurePluginFilesTreeSafe(rootPath, targetPath);
            if (!File.Exists(targetPath))
            {
                throw new FileNotFoundException("The requested plugin file does not exist.", normalizedPath);
            }

            var fileInfo = new FileInfo(targetPath);
            if (fileInfo.Length > MaxPluginFileBytes)
            {
                throw new InvalidOperationException("The requested plugin file is too large to read through the SDK.");
            }

            var encoding = NormalizePluginFileEncoding(request?.Encoding);
            var bytes = File.ReadAllBytes(targetPath);
            var content = encoding == "base64"
                ? Convert.ToBase64String(bytes)
                : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return new PluginSdkFileContentState(
                normalizedPath,
                content,
                encoding,
                bytes.LongLength,
                fileInfo.LastWriteTimeUtc);
        }
    }

    public PluginSdkFileMutationState WritePluginSdkFile(string pluginId, PluginSdkFileWriteRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("A plugin file write payload is required.");
        }

        var context = EnsurePluginPermission(pluginId, PermissionFiles);
        lock (_sdkFileGate)
        {
            var rootPath = GetPluginFilesDirectory(context.PluginId);
            Directory.CreateDirectory(rootPath);
            var normalizedPath = NormalizePluginFilePath(request.Path, allowEmpty: false);
            var targetPath = ResolvePluginFilesPath(rootPath, normalizedPath);
            EnsurePluginFilesTreeSafe(rootPath, targetPath);
            if (Directory.Exists(targetPath))
            {
                throw new InvalidOperationException("The requested plugin file path is a directory.");
            }

            var bytes = DecodePluginFileContent(request.Content, request.Encoding);
            var existingLength = File.Exists(targetPath) ? new FileInfo(targetPath).Length : 0;
            var resultingLength = request.Append ? existingLength + bytes.LongLength : bytes.LongLength;
            if (resultingLength > MaxPluginFileBytes)
            {
                throw new InvalidOperationException("A single plugin file cannot exceed 8 MB.");
            }

            if (File.Exists(targetPath) && !request.Append && !request.Overwrite)
            {
                throw new InvalidOperationException("The plugin file already exists and overwrite was not enabled.");
            }

            var usedBytes = GetPluginFilesUsage(rootPath);
            var projectedUsage = request.Append
                ? usedBytes + bytes.LongLength
                : usedBytes - existingLength + bytes.LongLength;
            if (projectedUsage > MaxPluginFilesBytes)
            {
                throw new InvalidOperationException("The plugin file storage quota of 32 MB would be exceeded.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? rootPath);
            EnsurePluginFilesTreeSafe(rootPath, targetPath);
            if (request.Append)
            {
                using var stream = new FileStream(targetPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            }
            else
            {
                File.WriteAllBytes(targetPath, bytes);
            }

            return BuildPluginFileMutationState(rootPath, targetPath, normalizedPath);
        }
    }

    public PluginSdkFileMutationState CreatePluginSdkDirectory(string pluginId, PluginSdkFilePathRequest? request)
    {
        var context = EnsurePluginPermission(pluginId, PermissionFiles);
        lock (_sdkFileGate)
        {
            var rootPath = GetPluginFilesDirectory(context.PluginId);
            Directory.CreateDirectory(rootPath);
            var normalizedPath = NormalizePluginFilePath(request?.Path, allowEmpty: true);
            var targetPath = ResolvePluginFilesPath(rootPath, normalizedPath);
            EnsurePluginFilesTreeSafe(rootPath, targetPath);
            Directory.CreateDirectory(targetPath);
            EnsurePluginFilesTreeSafe(rootPath, targetPath);
            return BuildPluginFileMutationState(rootPath, targetPath, normalizedPath);
        }
    }

    public PluginSdkFileMutationState DeletePluginSdkFile(string pluginId, PluginSdkFilePathRequest? request)
    {
        var context = EnsurePluginPermission(pluginId, PermissionFiles);
        lock (_sdkFileGate)
        {
            var rootPath = GetPluginFilesDirectory(context.PluginId);
            var normalizedPath = NormalizePluginFilePath(request?.Path, allowEmpty: false);
            var targetPath = ResolvePluginFilesPath(rootPath, normalizedPath);
            EnsurePluginFilesTreeSafe(rootPath, targetPath);
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
            else if (Directory.Exists(targetPath))
            {
                Directory.Delete(targetPath, request?.Recursive ?? false);
            }

            return BuildPluginFileMutationState(rootPath, targetPath, normalizedPath);
        }
    }

    public PluginSdkFileMutationState MovePluginSdkFile(string pluginId, PluginSdkFileTransferRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("A plugin file move payload is required.");
        }

        var context = EnsurePluginPermission(pluginId, PermissionFiles);
        lock (_sdkFileGate)
        {
            var rootPath = GetPluginFilesDirectory(context.PluginId);
            var sourceRelativePath = NormalizePluginFilePath(request.SourcePath, allowEmpty: false);
            var destinationRelativePath = NormalizePluginFilePath(request.DestinationPath, allowEmpty: false);
            var sourcePath = ResolvePluginFilesPath(rootPath, sourceRelativePath);
            var destinationPath = ResolvePluginFilesPath(rootPath, destinationRelativePath);
            EnsurePluginFilesTreeSafe(rootPath, sourcePath);
            EnsurePluginFilesTreeSafe(rootPath, destinationPath);

            if (File.Exists(sourcePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? rootPath);
                File.Move(sourcePath, destinationPath, request.Overwrite);
            }
            else if (Directory.Exists(sourcePath))
            {
                if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                {
                    throw new InvalidOperationException("The destination plugin path already exists.");
                }

                var sourcePrefix = sourcePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (destinationPath.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("A plugin directory cannot be moved inside itself.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? rootPath);
                Directory.Move(sourcePath, destinationPath);
            }
            else
            {
                throw new FileNotFoundException("The source plugin path does not exist.", sourceRelativePath);
            }

            return BuildPluginFileMutationState(rootPath, destinationPath, destinationRelativePath);
        }
    }

    public PluginSdkFileMutationState CopyPluginSdkFile(string pluginId, PluginSdkFileTransferRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("A plugin file copy payload is required.");
        }

        var context = EnsurePluginPermission(pluginId, PermissionFiles);
        lock (_sdkFileGate)
        {
            var rootPath = GetPluginFilesDirectory(context.PluginId);
            var sourceRelativePath = NormalizePluginFilePath(request.SourcePath, allowEmpty: false);
            var destinationRelativePath = NormalizePluginFilePath(request.DestinationPath, allowEmpty: false);
            var sourcePath = ResolvePluginFilesPath(rootPath, sourceRelativePath);
            var destinationPath = ResolvePluginFilesPath(rootPath, destinationRelativePath);
            EnsurePluginFilesTreeSafe(rootPath, sourcePath);
            EnsurePluginFilesTreeSafe(rootPath, destinationPath);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("The source plugin file does not exist.", sourceRelativePath);
            }

            var sourceLength = new FileInfo(sourcePath).Length;
            var destinationLength = File.Exists(destinationPath) ? new FileInfo(destinationPath).Length : 0;
            if (GetPluginFilesUsage(rootPath) - destinationLength + sourceLength > MaxPluginFilesBytes)
            {
                throw new InvalidOperationException("The plugin file storage quota of 32 MB would be exceeded.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? rootPath);
            File.Copy(sourcePath, destinationPath, request.Overwrite);
            return BuildPluginFileMutationState(rootPath, destinationPath, destinationRelativePath);
        }
    }

    private PluginStoreCommunityRuntimePluginState? BuildCommunityRuntimePluginState(
        string pluginId,
        InstalledCommunityPluginData installedPlugin)
    {
        var normalizedPluginId = NormalizePluginId(pluginId);
        if (normalizedPluginId.Length == 0)
        {
            return null;
        }

        var pluginDirectory = GetCommunityPluginDirectory(normalizedPluginId);
        if (!Directory.Exists(pluginDirectory))
        {
            return null;
        }

        var runtimePlugin = installedPlugin;
        var title = normalizedPluginId;
        var description = "Community plugin.";
        try
        {
            var manifestPath = ResolvePackagePath(pluginDirectory, ManifestFileName);
            if (File.Exists(manifestPath))
            {
                var manifest = JsonSerializer.Deserialize<PluginManifestData>(
                    File.ReadAllText(manifestPath),
                    JsonOptions) ?? new PluginManifestData();
                title = string.IsNullOrWhiteSpace(manifest.Name) ? title : manifest.Name.Trim();
                description = string.IsNullOrWhiteSpace(manifest.Description)
                    ? description
                    : manifest.Description.Trim();

                if (TryBuildInstalledPluginDataFromManifest(normalizedPluginId, pluginDirectory, out var manifestPlugin))
                {
                    runtimePlugin = manifestPlugin;
                }
            }
        }
        catch
        {
        }

        return new PluginStoreCommunityRuntimePluginState(
            normalizedPluginId,
            title,
            description,
            runtimePlugin.Version,
            runtimePlugin.SdkVersion,
            runtimePlugin.EntryPoint,
            $"api/plugin-store/community/{Uri.EscapeDataString(normalizedPluginId)}/files/{Uri.EscapeDataString(runtimePlugin.EntryPoint).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}",
            runtimePlugin.Permissions);
    }

    private InstalledCommunityPluginContext EnsurePluginPermission(string pluginId, string permission)
    {
        var context = EnsureInstalledCommunityPlugin(pluginId);
        if (!HasPluginPermission(context.Data, permission))
        {
            throw new InvalidOperationException($"Plugin {context.PluginId} does not declare the required {permission} permission.");
        }

        return context;
    }

    private InstalledCommunityPluginContext EnsureInstalledCommunityPlugin(string pluginId)
    {
        var normalizedPluginId = NormalizePluginId(pluginId);
        if (normalizedPluginId.Length == 0)
        {
            throw new InvalidOperationException("A community plugin ID is required.");
        }

        var pluginDirectory = GetCommunityPluginDirectory(normalizedPluginId);
        var installedState = LoadInstalledState();
        if (installedState.Plugins.TryGetValue(normalizedPluginId, out var installedPlugin) &&
            Directory.Exists(pluginDirectory))
        {
            return new InstalledCommunityPluginContext(normalizedPluginId, installedPlugin, pluginDirectory);
        }

        if (Directory.Exists(pluginDirectory) &&
            TryBuildInstalledPluginDataFromManifest(normalizedPluginId, pluginDirectory, out var manifestPlugin))
        {
            return new InstalledCommunityPluginContext(normalizedPluginId, manifestPlugin, pluginDirectory);
        }

        throw new InvalidOperationException("The requested community plugin is not installed.");
    }

    private static bool HasPluginPermission(InstalledCommunityPluginData plugin, string permission)
    {
        return plugin.Permissions.Any(entry => string.Equals(entry, permission, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryBuildInstalledPluginDataFromManifest(
        string pluginId,
        string pluginDirectory,
        out InstalledCommunityPluginData installedPlugin)
    {
        installedPlugin = new InstalledCommunityPluginData();

        try
        {
            var manifestPath = ResolvePackagePath(pluginDirectory, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            var manifest = JsonSerializer.Deserialize<PluginManifestData>(
                File.ReadAllText(manifestPath),
                JsonOptions) ?? new PluginManifestData();
            var manifestId = NormalizePluginId(manifest.Id);
            var sdkVersion = NormalizeVersion(manifest.SdkVersion);
            var entryPoint = NormalizePackageRelativePath(manifest.EntryPoint, "dist/index.js");
            if (!string.Equals(manifestId, pluginId, StringComparison.OrdinalIgnoreCase) ||
                !IsSupportedSdkVersion(sdkVersion) ||
                !File.Exists(ResolvePackagePath(pluginDirectory, entryPoint)))
            {
                return false;
            }

            var version = NormalizeVersion(manifest.Version);
            installedPlugin = new InstalledCommunityPluginData
            {
                Version = version,
                ManifestVersion = version,
                SdkVersion = sdkVersion,
                EntryPoint = entryPoint,
                Permissions = NormalizePermissions(manifest.Permissions),
                InstalledAtUtc = DateTimeOffset.MinValue
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private JsonElement LoadPluginSettingsElement(string pluginId)
    {
        var settingsPath = GetPluginSettingsPath(pluginId);
        if (!File.Exists(settingsPath))
        {
            return CreateEmptyObjectElement();
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return document.RootElement.ValueKind is JsonValueKind.Object
                ? document.RootElement.Clone()
                : CreateEmptyObjectElement();
        }
        catch
        {
            return CreateEmptyObjectElement();
        }
    }

    private static JsonElement CreateEmptyObjectElement()
    {
        return JsonSerializer.SerializeToElement(new Dictionary<string, object>(), JsonOptions);
    }

    private IReadOnlyDictionary<string, bool> LoadPluginSecretFlags(string pluginId)
    {
        return LoadPluginSecretStore(pluginId).Secrets
            .ToDictionary(secret => secret.Key, secret => true, StringComparer.OrdinalIgnoreCase);
    }

    private PluginSecretStoreData LoadPluginSecretStore(string pluginId)
    {
        var secretsPath = GetPluginSecretsPath(pluginId);
        if (!File.Exists(secretsPath))
        {
            return new PluginSecretStoreData();
        }

        try
        {
            var state = JsonSerializer.Deserialize<PluginSecretStoreData>(
                File.ReadAllText(secretsPath),
                JsonOptions) ?? new PluginSecretStoreData();
            state.Secrets = new Dictionary<string, string>(
                state.Secrets ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch
        {
            return new PluginSecretStoreData();
        }
    }

    private void SavePluginSecretStore(string pluginId, PluginSecretStoreData state)
    {
        var secretsPath = GetPluginSecretsPath(pluginId);
        Directory.CreateDirectory(Path.GetDirectoryName(secretsPath) ?? GetPluginSdkDataDirectory(pluginId));
        state.Secrets = new Dictionary<string, string>(
            state.Secrets ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        File.WriteAllText(secretsPath, JsonSerializer.Serialize(state, JsonOptions), Encoding.UTF8);
    }

    private string LoadPluginSecretValue(string pluginId, string key)
    {
        var secretStore = LoadPluginSecretStore(pluginId);
        return secretStore.Secrets.TryGetValue(key, out var protectedSecret)
            ? UnprotectSecret(protectedSecret)
            : string.Empty;
    }

    private static string ProtectSecret(string value)
    {
        var plainBytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return "dpapi:" + Convert.ToBase64String(protectedBytes);
    }

    private static string UnprotectSecret(string protectedValue)
    {
        if (!protectedValue.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedValue["dpapi:".Length..]);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private string GetPluginSdkDataDirectory(string pluginId)
    {
        var normalizedPluginId = NormalizePluginId(pluginId);
        var targetPath = Path.GetFullPath(Path.Combine(_sdkDataRootPath, normalizedPluginId));
        EnsureWithinPathRoot(targetPath, _sdkDataRootPath);
        return targetPath;
    }

    private string GetPluginSettingsPath(string pluginId)
    {
        return Path.Combine(GetPluginSdkDataDirectory(pluginId), "settings.json");
    }

    private string GetPluginSecretsPath(string pluginId)
    {
        return Path.Combine(GetPluginSdkDataDirectory(pluginId), "secrets.json");
    }

    private static string BuildNetworkRequestBody(JsonElement? body)
    {
        if (body is null || body.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return string.Empty;
        }

        return body.Value.ValueKind is JsonValueKind.String
            ? body.Value.GetString() ?? string.Empty
            : body.Value.GetRawText();
    }

    private static string NormalizeSdkNetworkMethod(string? method)
    {
        var normalized = (method ?? "GET").Trim().ToUpperInvariant();
        return normalized is "GET" or "POST" or "PUT" or "PATCH" or "DELETE"
            ? normalized
            : throw new InvalidOperationException("Plugin network requests must use GET, POST, PUT, PATCH, or DELETE.");
    }

    private static bool IsBlockedNetworkHeader(string headerName)
    {
        return headerName.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
            headerName.Equals("host", StringComparison.OrdinalIgnoreCase) ||
            headerName.Equals("content-length", StringComparison.OrdinalIgnoreCase) ||
            headerName.Equals("connection", StringComparison.OrdinalIgnoreCase) ||
            headerName.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAuthorizationScheme(string? scheme)
    {
        var normalized = string.IsNullOrWhiteSpace(scheme) ? "Bearer" : scheme.Trim();
        if (normalized.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new InvalidOperationException("The plugin network authorization scheme is invalid.");
        }

        return normalized;
    }

    private static async Task<string> ReadNetworkResponseBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            if (memory.Length + read > MaxPluginNetworkResponseBytes)
            {
                throw new InvalidOperationException("Plugin network response body is too large.");
            }

            memory.Write(buffer, 0, read);
        }

        return ResolveResponseEncoding(content).GetString(memory.ToArray());
    }

    private static Encoding ResolveResponseEncoding(HttpContent content)
    {
        var charset = content.Headers.ContentType?.CharSet;
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim('"'));
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private static string NormalizeSdkKey(string? key, string label)
    {
        var normalized = (key ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0 ||
            normalized.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new InvalidOperationException($"A valid plugin {label} is required.");
        }

        return normalized;
    }

    private PluginStorePluginState BuildCommunityPluginState(
        CommunityCatalogPluginData plugin,
        InstalledCommunityPluginsState installedState)
    {
        var normalizedId = NormalizePluginId(plugin.Id);
        var pluginDirectory = GetCommunityPluginDirectory(normalizedId);
        installedState.Plugins.TryGetValue(normalizedId, out var installedPlugin);
        var isInstalled = Directory.Exists(pluginDirectory) ||
            (installedPlugin is not null && !string.IsNullOrWhiteSpace(installedPlugin.Version));
        var installedVersion = installedPlugin?.Version ?? string.Empty;
        var hasUpdate = isInstalled &&
            !string.IsNullOrWhiteSpace(installedVersion) &&
            !string.Equals(
                NormalizeVersion(installedVersion),
                NormalizeVersion(plugin.Version),
                StringComparison.OrdinalIgnoreCase);
        var sdkVersion = installedPlugin?.SdkVersion ?? string.Empty;
        var entryPoint = installedPlugin?.EntryPoint ?? string.Empty;
        var permissions = installedPlugin?.Permissions ?? [];
        var hasPackageSource = plugin.HasPackageSource;
        var hasPackageChecksum = !string.IsNullOrWhiteSpace(plugin.PackageSha256);
        var canInstall = hasPackageSource && hasPackageChecksum;

        var statusText = !hasPackageSource
            ? "Listed in the catalog, but not downloadable yet."
            : !hasPackageChecksum
                ? "Listed in the catalog, but blocked because the package checksum is missing."
                : !isInstalled
                    ? "Ready to install from the community catalog."
                    : hasUpdate
                        ? "Installed locally. A newer catalog version is available."
                        : "Installed locally and up to date.";

        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(plugin.Category))
        {
            tags.Add(plugin.Category);
        }

        if (plugin.Tags.Length > 0)
        {
            tags.AddRange(plugin.Tags);
        }

        tags.Add(isInstalled ? "Installed" : "Community");
        if (hasUpdate)
        {
            tags.Add("Update");
        }

        return new PluginStorePluginState(
            normalizedId,
            plugin.Title,
            plugin.Description,
            Source: "Community",
            Author: plugin.Author,
            Category: string.IsNullOrWhiteSpace(plugin.Category) ? "Community" : plugin.Category,
            Version: plugin.Version,
            InstalledVersion: installedVersion,
            SdkVersion: sdkVersion,
            EntryPoint: entryPoint,
            Permissions: permissions,
            IsBuiltIn: false,
            IsInstalled: isInstalled,
            IsEnabled: isInstalled,
            CanToggleVisibility: false,
            CanInstall: canInstall,
            CanUninstall: isInstalled,
            HasUpdate: hasUpdate,
            StatusText: statusText,
            Tags: tags,
            Images: plugin.Images
                .Where(image => !string.IsNullOrWhiteSpace(image))
                .Select(image => new PluginStoreImageState(image, $"{plugin.Title} screenshot"))
                .ToArray());
    }

    private IReadOnlyList<PluginStoreImageState> BuildBuiltInImages(string pluginId, string title)
    {
        return TryResolveBuiltInImagePath(pluginId, out _)
            ? [new PluginStoreImageState($"api/plugin-store/images/built-in/{Uri.EscapeDataString(pluginId)}", $"{title} preview")]
            : [];
    }

    private static string GetImageContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            _ => "image/png"
        };
    }

    private static string GetContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".js" or ".mjs" or ".cjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".png" => "image/png",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
    }

    private async Task<CommunityCatalogSnapshot> LoadCommunityCatalogAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_catalogPath))
        {
            var statusText = !_enableCommunityCatalogBootstrap
                ? "No community catalog has been cached yet. Press Refresh to download the default GitHub catalog."
                : string.IsNullOrWhiteSpace(_communityCatalogBootstrapError)
                    ? "The default GitHub catalog is not cached yet. TFS will download it automatically on first use, or you can press Refresh to retry."
                    : $"The community catalog could not be downloaded automatically ({_communityCatalogBootstrapError}). Press Refresh to retry.";

            return new CommunityCatalogSnapshot(
                Available: false,
                Title: "TFS Store",
                Description: "Built-in plugins are always available here. Community plugins are loaded from the connected catalog when it becomes available.",
                StatusText: statusText,
                CatalogDirectory: Path.GetDirectoryName(_catalogPath) ?? _rootPath,
                Plugins: []);
        }

        try
        {
            await using var stream = File.OpenRead(_catalogPath);
            var data = await JsonSerializer.DeserializeAsync<CommunityCatalogFileData>(stream, JsonOptions, cancellationToken)
                ?? new CommunityCatalogFileData();
            var catalogDirectory = Path.GetDirectoryName(_catalogPath) ?? _rootPath;
            var plugins = (data.Plugins ?? [])
                .Select(plugin => TryNormalizeCommunityPlugin(plugin, out var normalizedPlugin) ? normalizedPlugin : null)
                .Where(plugin => plugin is not null)
                .Cast<CommunityCatalogPluginData>()
                .Where(plugin => !string.IsNullOrWhiteSpace(plugin.Id))
                .GroupBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

            return new CommunityCatalogSnapshot(
                Available: true,
                Title: string.IsNullOrWhiteSpace(data.Title) ? "TFS Community" : data.Title.Trim(),
                Description: string.IsNullOrWhiteSpace(data.Description)
                    ? "Community plugins published for Tools for Steam."
                    : data.Description.Trim(),
                StatusText: plugins.Length == 0
                    ? "The community catalog is connected, but it does not publish any plugins yet."
                    : $"{plugins.Length} community plugin{(plugins.Length == 1 ? string.Empty : "s")} are available from the connected catalog.",
                CatalogDirectory: catalogDirectory,
                Plugins: plugins);
        }
        catch (Exception exception)
        {
            return new CommunityCatalogSnapshot(
                Available: false,
                Title: "TFS Store",
                Description: "Built-in plugins are always available here. Fix the community catalog file to restore downloads.",
                StatusText: $"The community catalog could not be read ({exception.Message}).",
                CatalogDirectory: Path.GetDirectoryName(_catalogPath) ?? _rootPath,
                Plugins: []);
        }
    }

    private static bool TryNormalizeCommunityPlugin(
        CommunityCatalogPluginData? plugin,
        out CommunityCatalogPluginData normalizedPlugin)
    {
        try
        {
            normalizedPlugin = NormalizeCommunityPlugin(plugin);
            return true;
        }
        catch
        {
            normalizedPlugin = new CommunityCatalogPluginData();
            return false;
        }
    }

    private static CommunityCatalogPluginData NormalizeCommunityPlugin(CommunityCatalogPluginData? plugin)
    {
        return new CommunityCatalogPluginData
        {
            Id = NormalizePluginId(plugin?.Id),
            Title = (plugin?.Title ?? string.Empty).Trim(),
            Description = (plugin?.Description ?? string.Empty).Trim(),
            Author = string.IsNullOrWhiteSpace(plugin?.Author) ? "Community" : plugin!.Author.Trim(),
            Category = (plugin?.Category ?? string.Empty).Trim(),
            Version = string.IsNullOrWhiteSpace(plugin?.Version) ? "0.0.0" : plugin!.Version.Trim(),
            Images = (plugin?.Images ?? [])
                .Where(image => !string.IsNullOrWhiteSpace(image))
                .Select(image => image.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Tags = (plugin?.Tags ?? [])
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            PackagePath = (plugin?.PackagePath ?? string.Empty).Trim(),
            PackageUrl = (plugin?.PackageUrl ?? string.Empty).Trim(),
            PackageSha256 = NormalizeSha256(plugin?.PackageSha256),
            HomepageUrl = (plugin?.HomepageUrl ?? string.Empty).Trim(),
            RepositoryUrl = (plugin?.RepositoryUrl ?? string.Empty).Trim()
        };
    }

    private async Task<string> ResolvePackageZipAsync(
        CommunityCatalogSnapshot catalog,
        CommunityCatalogPluginData plugin,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(plugin.PackagePath))
        {
            if (Path.IsPathRooted(plugin.PackagePath))
            {
                throw new InvalidOperationException("Local community package paths must be relative to the catalog file.");
            }

            var packagePath = Path.GetFullPath(Path.Combine(catalog.CatalogDirectory, plugin.PackagePath));
            EnsureWithinPathRoot(packagePath, catalog.CatalogDirectory);
            EnsureZipFileName(packagePath);
            if (!File.Exists(packagePath))
            {
                throw new InvalidOperationException("The configured community package could not be found.");
            }

            ValidatePackageZip(packagePath);
            return packagePath;
        }

        if (!Uri.TryCreate(plugin.PackageUrl, UriKind.Absolute, out var packageUri))
        {
            throw new InvalidOperationException("The configured community package URL is invalid.");
        }

        if (packageUri.Scheme is not "http" and not "https")
        {
            throw new InvalidOperationException("Remote community packages must use HTTP or HTTPS.");
        }

        EnsureZipFileName(packageUri.AbsolutePath);

        var tempDirectory = Path.Combine(_rootPath, "tmp");
        Directory.CreateDirectory(tempDirectory);

        var destinationPath = Path.Combine(tempDirectory, $"{plugin.Id}-{Guid.NewGuid():N}.zip");
        using var request = new HttpRequestMessage(HttpMethod.Get, packageUri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxCommunityPackageBytes)
        {
            throw new InvalidOperationException("The community package is too large.");
        }

        await using var packageStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destinationStream = File.Create(destinationPath);
        await CopyPackageWithLimitAsync(packageStream, destinationStream, cancellationToken);
        await destinationStream.FlushAsync(cancellationToken);
        ValidatePackageZip(destinationPath);
        return destinationPath;
    }

    private async Task SyncCommunityCatalogAsync(CancellationToken cancellationToken)
    {
        await _catalogSyncSemaphore.WaitAsync(cancellationToken);
        try
        {
            var catalogUrl = GetCommunityCatalogUrl();
            using var request = new HttpRequestMessage(HttpMethod.Get, catalogUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > MaxCommunityCatalogBytes)
            {
                throw new InvalidOperationException("The community catalog is too large.");
            }

            var catalogBytes = StripUtf8Bom(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            if (catalogBytes.Length > MaxCommunityCatalogBytes)
            {
                throw new InvalidOperationException("The community catalog is too large.");
            }

            _ = JsonSerializer.Deserialize<CommunityCatalogFileData>(catalogBytes, JsonOptions)
                ?? throw new InvalidOperationException("The community catalog is empty.");

            Directory.CreateDirectory(_rootPath);
            await File.WriteAllBytesAsync(_catalogPath, catalogBytes, cancellationToken);
            _communityCatalogBootstrapError = string.Empty;
        }
        finally
        {
            _catalogSyncSemaphore.Release();
        }
    }

    private async Task EnsureCommunityCatalogAvailableAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_catalogPath))
        {
            return;
        }

        Task bootstrapTask;
        lock (_catalogBootstrapGate)
        {
            _communityCatalogBootstrapTask ??= BootstrapCommunityCatalogAsync();
            bootstrapTask = _communityCatalogBootstrapTask;
        }

        await bootstrapTask.WaitAsync(cancellationToken);
    }

    private async Task BootstrapCommunityCatalogAsync()
    {
        try
        {
            await SyncCommunityCatalogAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _communityCatalogBootstrapError = exception.Message;
        }
    }

    private void EnsureBuiltInImageCache()
    {
        Directory.CreateDirectory(_builtInImagesRootPath);

        foreach (var plugin in SteamLoaderPluginCatalog.Definitions)
        {
            var normalizedPluginId = NormalizePluginId(plugin.Id);
            if (normalizedPluginId.Length == 0)
            {
                continue;
            }

            var imagePath = Path.Combine(_builtInImagesRootPath, $"{normalizedPluginId}.svg");
            if (File.Exists(imagePath))
            {
                continue;
            }

            File.WriteAllText(
                imagePath,
                BuildBuiltInPreviewSvg(normalizedPluginId, plugin.Title, plugin.Description),
                Encoding.UTF8);
        }
    }

    private bool TryResolveBuiltInImagePath(string pluginId, out string imagePath)
    {
        imagePath = string.Empty;

        var normalizedPluginId = NormalizePluginId(pluginId);
        if (normalizedPluginId.Length == 0)
        {
            return false;
        }

        var candidatePath = Path.GetFullPath(Path.Combine(_builtInImagesRootPath, $"{normalizedPluginId}.svg"));
        EnsureWithinPathRoot(candidatePath, _builtInImagesRootPath);
        if (!File.Exists(candidatePath))
        {
            return false;
        }

        imagePath = candidatePath;
        return true;
    }

    private static string BuildBuiltInPreviewSvg(string pluginId, string title, string description)
    {
        var accent = BuiltInImageAccents.TryGetValue(pluginId, out var configuredAccent)
            ? configuredAccent
            : "#60A5FA";
        var escapedTitle = EscapeSvgText(title);
        var escapedDescription = EscapeSvgText(TruncateForSvg(description, 88));
        var escapedLabel = EscapeSvgText(pluginId.Replace('-', ' ').ToUpperInvariant());

        return $$"""
                 <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1280 720" role="img" aria-label="{{escapedTitle}}">
                   <defs>
                     <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
                       <stop offset="0%" stop-color="#0B1120" />
                       <stop offset="100%" stop-color="#172554" />
                     </linearGradient>
                     <linearGradient id="accent" x1="0" y1="0" x2="1" y2="1">
                       <stop offset="0%" stop-color="{{accent}}" stop-opacity="1" />
                       <stop offset="100%" stop-color="{{accent}}" stop-opacity="0.18" />
                     </linearGradient>
                   </defs>
                   <rect width="1280" height="720" rx="36" fill="url(#bg)" />
                   <circle cx="1100" cy="120" r="210" fill="url(#accent)" />
                   <circle cx="1190" cy="610" r="180" fill="{{accent}}" fill-opacity="0.14" />
                   <rect x="74" y="74" width="1132" height="572" rx="32" fill="#07101D" fill-opacity="0.56" stroke="{{accent}}" stroke-opacity="0.28" />
                   <text x="116" y="176" fill="{{accent}}" font-family="Segoe UI, Arial, sans-serif" font-size="34" font-weight="700" letter-spacing="5">{{escapedLabel}}</text>
                   <text x="116" y="306" fill="#F8FAFC" font-family="Segoe UI, Arial, sans-serif" font-size="86" font-weight="700">{{escapedTitle}}</text>
                   <text x="116" y="386" fill="#CBD5E1" font-family="Segoe UI, Arial, sans-serif" font-size="34">{{escapedDescription}}</text>
                   <text x="116" y="560" fill="#E2E8F0" font-family="Segoe UI, Arial, sans-serif" font-size="28">Built-in plugin preview</text>
                 </svg>
                 """;
    }

    private static string EscapeSvgText(string value)
    {
        return WebUtility.HtmlEncode(value ?? string.Empty);
    }

    private static string TruncateForSvg(string value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return $"{normalized[..Math.Max(0, maxLength - 1)].TrimEnd()}...";
    }

    private static byte[] StripUtf8Bom(byte[] bytes)
    {
        return bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;
    }

    private string GetCommunityCatalogUrl()
    {
        var catalogUrl = DefaultCommunityCatalogUrl;
        if (File.Exists(_catalogSourcePath))
        {
            var source = JsonSerializer.Deserialize<CommunityCatalogSourceFileData>(
                File.ReadAllText(_catalogSourcePath),
                JsonOptions);
            if (!string.IsNullOrWhiteSpace(source?.CatalogUrl))
            {
                catalogUrl = source.CatalogUrl.Trim();
            }
        }

        if (!Uri.TryCreate(catalogUrl, UriKind.Absolute, out var catalogUri) ||
            catalogUri.Scheme is not "http" and not "https")
        {
            throw new InvalidOperationException("The community catalog URL must be an HTTP or HTTPS URL.");
        }

        return catalogUri.ToString();
    }

    private static void EnsureZipFileName(string pathOrUrlPath)
    {
        if (!Path.GetExtension(pathOrUrlPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Community packages must be zip files.");
        }
    }

    private static void ValidatePackageZip(string zipPath)
    {
        EnsureZipFileName(zipPath);
        var info = new FileInfo(zipPath);
        if (!info.Exists)
        {
            throw new InvalidOperationException("The configured community package could not be found.");
        }

        if (info.Length <= 0)
        {
            throw new InvalidOperationException("The community package is empty.");
        }

        if (info.Length > MaxCommunityPackageBytes)
        {
            throw new InvalidOperationException("The community package is too large.");
        }
    }

    private static async Task CopyPackageWithLimitAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long totalBytes = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            totalBytes += read;
            if (totalBytes > MaxCommunityPackageBytes)
            {
                throw new InvalidOperationException("The community package is too large.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void ValidatePackageHash(string zipPath, CommunityCatalogPluginData plugin)
    {
        var expectedHash = NormalizeSha256(plugin.PackageSha256);
        if (expectedHash.Length == 0)
        {
            throw new InvalidOperationException("Community packages must publish a SHA-256 checksum.");
        }

        using var stream = File.OpenRead(zipPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The community package checksum does not match the catalog.");
        }
    }

    private static PluginManifestData ValidatePluginManifest(
        string pluginRoot,
        CommunityCatalogPluginData catalogPlugin)
    {
        var manifestPath = ResolvePackagePath(pluginRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"Community packages must include {ManifestFileName}.");
        }

        var manifest = JsonSerializer.Deserialize<PluginManifestData>(
            File.ReadAllText(manifestPath),
            JsonOptions) ?? new PluginManifestData();
        var manifestId = NormalizePluginId(manifest.Id);
        var catalogId = NormalizePluginId(catalogPlugin.Id);
        if (manifestId.Length == 0)
        {
            throw new InvalidOperationException("The community plugin manifest requires an id.");
        }

        if (!string.Equals(manifestId, catalogId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The community plugin manifest id does not match the catalog entry.");
        }

        var manifestVersion = NormalizeVersion(manifest.Version);
        if (manifestVersion.Length == 0)
        {
            throw new InvalidOperationException("The community plugin manifest requires a version.");
        }

        if (!string.Equals(
            NormalizeVersion(catalogPlugin.Version),
            manifestVersion,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The community plugin manifest version does not match the catalog entry.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new InvalidOperationException("The community plugin manifest requires a name.");
        }

        var sdkVersion = NormalizeVersion(manifest.SdkVersion);
        if (!IsSupportedSdkVersion(sdkVersion))
        {
            throw new InvalidOperationException($"This plugin requires an unsupported TFS plugin SDK version ({sdkVersion}).");
        }

        var entryPoint = NormalizePackageRelativePath(manifest.EntryPoint, "dist/index.js");
        var entryPointPath = ResolvePackagePath(pluginRoot, entryPoint);
        if (!File.Exists(entryPointPath))
        {
            throw new InvalidOperationException("The community plugin entry point could not be found.");
        }

        var extension = Path.GetExtension(entryPointPath);
        if (!extension.Equals(".js", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The community plugin entry point must be a JavaScript file.");
        }

        return manifest with
        {
            Id = manifestId,
            Name = string.IsNullOrWhiteSpace(manifest.Name) ? catalogPlugin.Title : manifest.Name.Trim(),
            Version = manifestVersion,
            SdkVersion = sdkVersion,
            EntryPoint = entryPoint,
            Permissions = NormalizePermissions(manifest.Permissions)
        };
    }

    private static bool IsSupportedSdkVersion(string sdkVersion)
    {
        if (string.IsNullOrWhiteSpace(sdkVersion))
        {
            return false;
        }

        var majorText = sdkVersion.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(majorText, out var majorVersion) && majorVersion == SupportedSdkMajorVersion;
    }

    private static string[] NormalizePermissions(IEnumerable<string>? permissions)
    {
        return (permissions ?? [])
            .Select(permission => (permission ?? string.Empty).Trim().ToLowerInvariant())
            .Where(permission => permission.Length > 0)
            .Select(permission =>
            {
                if (permission.Any(character =>
                    !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
                {
                    throw new InvalidOperationException("The community plugin manifest contains an invalid permission.");
                }

                if (!SupportedPluginPermissions.Contains(permission))
                {
                    throw new InvalidOperationException($"The community plugin manifest declares an unsupported permission ({permission}).");
                }

                return permission;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizePackageRelativePath(string? path, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(path) ? fallback : path.Trim();
        value = value.Replace('\\', '/').Trim('/');
        if (value.Length == 0 || Path.IsPathRooted(value))
        {
            throw new InvalidOperationException("The community plugin manifest contains an invalid relative path.");
        }

        if (value.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidOperationException("The community plugin manifest contains an invalid relative path.");
        }

        return value;
    }

    private static string ResolvePackagePath(string pluginRoot, string relativePath)
    {
        var normalizedRelativePath = NormalizePackageRelativePath(relativePath, relativePath);
        var path = Path.GetFullPath(Path.Combine(
            pluginRoot,
            normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureWithinPathRoot(path, pluginRoot);
        return path;
    }

    private string MoveExistingCommunityPluginToBackup(string targetDirectory)
    {
        EnsureWithinCommunityRoot(targetDirectory);

        var targetParent = Path.GetDirectoryName(targetDirectory)
            ?? throw new InvalidOperationException("Unable to resolve the target plugin directory.");
        Directory.CreateDirectory(targetParent);

        if (!Directory.Exists(targetDirectory))
        {
            return string.Empty;
        }

        var backupDirectory = Path.Combine(
            _rootPath,
            "tmp",
            $"{Path.GetFileName(targetDirectory)}-backup-{Guid.NewGuid():N}");
        EnsureWithinPathRoot(backupDirectory, _rootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(backupDirectory) ?? Path.Combine(_rootPath, "tmp"));
        Directory.Move(targetDirectory, backupDirectory);
        return backupDirectory;
    }

    private void MoveCommunityPluginReplacement(string targetDirectory, string sourceDirectory)
    {
        EnsureWithinCommunityRoot(targetDirectory);
        EnsureWithinPathRoot(sourceDirectory, _rootPath);
        if (Directory.Exists(targetDirectory))
        {
            throw new InvalidOperationException("The target community plugin directory already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)
            ?? throw new InvalidOperationException("Unable to resolve the target plugin directory."));
        Directory.Move(sourceDirectory, targetDirectory);
    }

    private void RestoreCommunityPluginBackup(string targetDirectory, string backupDirectory)
    {
        EnsureWithinCommunityRoot(targetDirectory);
        if (string.IsNullOrWhiteSpace(backupDirectory))
        {
            TryDeleteDirectory(targetDirectory);
            return;
        }

        if (!Directory.Exists(backupDirectory))
        {
            return;
        }

        EnsureWithinPathRoot(backupDirectory, _rootPath);
        TryDeleteDirectory(targetDirectory);
        Directory.Move(backupDirectory, targetDirectory);
    }

    private static string ResolveExtractedPluginRoot(string extractionDirectory)
    {
        var directories = Directory.GetDirectories(extractionDirectory);
        var files = Directory.GetFiles(extractionDirectory);
        if (directories.Length == 1 && files.Length == 0)
        {
            return directories[0];
        }

        return extractionDirectory;
    }

    private static void ExtractZipSafely(string zipPath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var fullDestinationDirectory = Path.GetFullPath(destinationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        var fileCount = 0;
        long extractedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName))
            {
                continue;
            }

            var targetPath = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!targetPath.StartsWith(fullDestinationDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The community package contains an invalid file path.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            fileCount += 1;
            if (fileCount > MaxCommunityPackageEntries)
            {
                throw new InvalidOperationException("The community package contains too many files.");
            }

            if (entry.Length > MaxCommunityPackageEntryBytes)
            {
                throw new InvalidOperationException("The community package contains a file that is too large.");
            }

            extractedBytes += entry.Length;
            if (extractedBytes > MaxCommunityPackageExtractedBytes)
            {
                throw new InvalidOperationException("The community package is too large after extraction.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private InstalledCommunityPluginsState LoadInstalledState()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_installedStatePath))
                {
                    return new InstalledCommunityPluginsState();
                }

                var json = File.ReadAllText(_installedStatePath);
                var state = JsonSerializer.Deserialize<InstalledCommunityPluginsState>(json, JsonOptions)
                    ?? new InstalledCommunityPluginsState();
                state.Plugins ??= new Dictionary<string, InstalledCommunityPluginData>(StringComparer.OrdinalIgnoreCase);
                return state;
            }
            catch
            {
                return new InstalledCommunityPluginsState();
            }
        }
    }

    private void SaveInstalledState(InstalledCommunityPluginsState state)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_installedStatePath) ?? _rootPath);
            state.Plugins ??= new Dictionary<string, InstalledCommunityPluginData>(StringComparer.OrdinalIgnoreCase);
            var tempPath = $"{_installedStatePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tempPath, JsonSerializer.Serialize(state, JsonOptions), Encoding.UTF8);
                if (File.Exists(_installedStatePath))
                {
                    File.Replace(tempPath, _installedStatePath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, _installedStatePath);
                }
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }
        }
    }

    private string GetCommunityPluginDirectory(string pluginId)
    {
        var normalizedPluginId = NormalizePluginId(pluginId);
        var targetPath = Path.GetFullPath(Path.Combine(_communityRootPath, normalizedPluginId));
        EnsureWithinCommunityRoot(targetPath);
        return targetPath;
    }

    private void EnsureWithinCommunityRoot(string path)
    {
        EnsureWithinPathRoot(path, _communityRootPath);
    }

    private static void EnsureWithinPathRoot(string path, string rootPath)
    {
        var normalizedRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);

        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The requested plugin path is outside the managed community directory.");
        }
    }

    private static string NormalizePluginId(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return string.Empty;
        }

        var filtered = new string(pluginId
            .Trim()
            .ToLowerInvariant()
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
            .ToArray());

        return filtered.Trim('.', '-', '_');
    }

    private static string NormalizeCatalogImageFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var normalized = Path.GetFileName(fileName.Trim());
        var extension = Path.GetExtension(normalized).ToLowerInvariant();
        if (normalized.Length == 0 || extension is not ".png" and not ".jpg" and not ".jpeg" and not ".webp" and not ".gif" and not ".svg")
        {
            return string.Empty;
        }

        return normalized;
    }

    private static string NormalizeInputAction(string? action)
    {
        var normalized = (action ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "up" or "down" or "left" or "right" or "a" or "b" or "search-back" or "previous-section" or "next-section"
            ? normalized
            : string.Empty;
    }

    private static string NormalizeVersion(string? version)
    {
        return (version ?? string.Empty).Trim();
    }

    private static string NormalizeSha256(string? hash)
    {
        var normalized = new string((hash ?? string.Empty)
            .Where(character => !char.IsWhiteSpace(character) && character != '-')
            .ToArray());

        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("Package SHA-256 values must contain 64 hexadecimal characters.");
        }

        return normalized.ToUpperInvariant();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record CommunityCatalogSnapshot(
        bool Available,
        string Title,
        string Description,
        string StatusText,
        string CatalogDirectory,
        IReadOnlyList<CommunityCatalogPluginData> Plugins);

    private sealed record CommunityCatalogFileData
    {
        public string? Title { get; init; }

        public string? Description { get; init; }

        public List<CommunityCatalogPluginData>? Plugins { get; init; }
    }

    private sealed record CommunityCatalogSourceFileData
    {
        public string? CatalogUrl { get; init; }
    }

    private sealed record CommunityCatalogPluginData
    {
        public string Id { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public string Author { get; init; } = string.Empty;

        public string Category { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;

        public string[] Images { get; init; } = [];

        public string[] Tags { get; init; } = [];

        public string PackagePath { get; init; } = string.Empty;

        public string PackageUrl { get; init; } = string.Empty;

        public string PackageSha256 { get; init; } = string.Empty;

        public string HomepageUrl { get; init; } = string.Empty;

        public string RepositoryUrl { get; init; } = string.Empty;

        public bool HasPackageSource =>
            !string.IsNullOrWhiteSpace(PackagePath) || !string.IsNullOrWhiteSpace(PackageUrl);
    }

    private sealed record PluginManifestData
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public string SdkVersion { get; init; } = string.Empty;

        public string EntryPoint { get; init; } = string.Empty;

        public string[] Permissions { get; init; } = [];
    }

    private sealed record InstalledCommunityPluginsState
    {
        public Dictionary<string, InstalledCommunityPluginData> Plugins { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record InstalledCommunityPluginContext(
        string PluginId,
        InstalledCommunityPluginData Data,
        string DirectoryPath);

    private sealed record InstalledCommunityPluginData
    {
        public string Version { get; init; } = string.Empty;

        public string ManifestVersion { get; init; } = string.Empty;

        public string SdkVersion { get; init; } = string.Empty;

        public string EntryPoint { get; init; } = string.Empty;

        public string[] Permissions { get; init; } = [];

        public DateTimeOffset InstalledAtUtc { get; init; }
    }

    private sealed record PluginSecretStoreData
    {
        public Dictionary<string, string> Secrets { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
