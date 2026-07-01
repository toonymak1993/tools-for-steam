using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SteamLoader.App.Infrastructure.PluginStore;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Models;
using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class PluginStoreServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [Fact]
    public async Task GetSnapshotAsync_WithoutCatalog_StillListsBuiltInPlugins()
    {
        var root = CreateTempRoot();

        try
        {
            var service = CreatePluginStoreService(root);

            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

            Assert.False(snapshot.CommunityCatalogAvailable);
            Assert.NotEmpty(snapshot.BuiltInPlugins);
            Assert.Contains(snapshot.BuiltInPlugins, plugin => plugin.Id == "smart-home");
            Assert.Empty(snapshot.CommunityPlugins);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task RefreshAsync_DownloadsCommunityCatalog()
    {
        var root = CreateTempRoot();
        var catalogJson = JsonSerializer.Serialize(
            new
            {
                title = "TFS Community",
                description = "Remote test catalog",
                plugins = new[]
                {
                    new
                    {
                        id = "sample-plugin",
                        title = "Sample Plugin",
                        description = "Community sample",
                        author = "Test Suite",
                        category = "Utility",
                        version = "1.2.3",
                        packageUrl = "https://example.test/packages/sample-plugin.zip",
                        packageSha256 = new string('0', 64),
                        images = new[] { "https://example.test/images/sample-plugin.png" },
                        tags = new[] { "sample" }
                    }
                }
            },
            JsonOptions);
        var handler = new CapturingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(catalogJson, Encoding.UTF8, "application/json")
        });

        try
        {
            var service = CreatePluginStoreService(root, new HttpClient(handler));

            var snapshot = await service.RefreshAsync(CancellationToken.None);

            Assert.True(snapshot.CommunityCatalogAvailable);
            var plugin = Assert.Single(snapshot.CommunityPlugins);
            Assert.Equal("sample-plugin", plugin.Id);
            Assert.True(plugin.CanInstall);
            Assert.Contains("tfs-plugin-database/main/catalog.json", handler.RequestUri?.ToString());
            Assert.True(File.Exists(Path.Combine(root, "plugin-store", "catalog.json")));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task InstallAndUninstallCommunityPlugin_LocalZipPackageUpdatesSnapshot()
    {
        var root = CreateTempRoot();

        try
        {
            var storeRoot = Path.Combine(root, "plugin-store");
            Directory.CreateDirectory(storeRoot);
            Directory.CreateDirectory(Path.Combine(storeRoot, "packages"));

            var zipPath = Path.Combine(storeRoot, "packages", "sample-plugin.zip");
            CreateSamplePluginZip(zipPath);
            File.WriteAllText(
                Path.Combine(storeRoot, "catalog.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        title = "TFS Community",
                        description = "Test catalog",
                        plugins = new[]
                        {
                            new
                            {
                                id = "sample-plugin",
                                title = "Sample Plugin",
                                description = "Community sample",
                                author = "Test Suite",
                                category = "Utility",
                                version = "1.2.3",
                                packagePath = "./packages/sample-plugin.zip",
                                packageSha256 = ComputeSha256(zipPath),
                                images = new[] { "api/plugin-store/images/catalog/sample-plugin.png" },
                                tags = new[] { "sample" }
                            }
                        }
                    },
                    JsonOptions));

            var service = CreatePluginStoreService(root);

            var beforeInstall = await service.GetSnapshotAsync(CancellationToken.None);
            Assert.Single(beforeInstall.CommunityPlugins);
            Assert.False(beforeInstall.CommunityPlugins[0].IsInstalled);

            var afterInstall = await service.InstallCommunityPluginAsync("sample-plugin", CancellationToken.None);
            Assert.True(afterInstall.CommunityPlugins[0].IsInstalled);
            Assert.True(Directory.Exists(Path.Combine(storeRoot, "community", "sample-plugin")));

            var afterUninstall = await service.UninstallCommunityPluginAsync("sample-plugin", CancellationToken.None);
            Assert.False(afterUninstall.CommunityPlugins[0].IsInstalled);
            Assert.False(Directory.Exists(Path.Combine(storeRoot, "community", "sample-plugin")));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task InstallCommunityPlugin_WithoutManifest_IsRejected()
    {
        var root = CreateTempRoot();

        try
        {
            var storeRoot = Path.Combine(root, "plugin-store");
            Directory.CreateDirectory(storeRoot);
            Directory.CreateDirectory(Path.Combine(storeRoot, "packages"));

            var zipPath = Path.Combine(storeRoot, "packages", "broken-plugin.zip");
            CreateSamplePluginZip(zipPath, includeManifest: false);
            WriteCatalog(
                storeRoot,
                "sample-plugin",
                "Sample Plugin",
                "1.2.3",
                "./packages/broken-plugin.zip");

            var service = CreatePluginStoreService(root);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.InstallCommunityPluginAsync("sample-plugin", CancellationToken.None));
            Assert.Contains("tfs-plugin.json", exception.Message);
            Assert.False(Directory.Exists(Path.Combine(storeRoot, "community", "sample-plugin")));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_CommunityPluginsWithoutImages_AreNotListed()
    {
        var root = CreateTempRoot();

        try
        {
            var storeRoot = Path.Combine(root, "plugin-store");
            Directory.CreateDirectory(storeRoot);
            File.WriteAllText(
                Path.Combine(storeRoot, "catalog.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        title = "TFS Community",
                        description = "Test catalog",
                        plugins = new[]
                        {
                            new
                            {
                                id = "sample-plugin",
                                title = "Sample Plugin",
                                description = "Community sample",
                                author = "Test Suite",
                                category = "Utility",
                                version = "1.2.3",
                                packagePath = "./packages/sample-plugin.zip",
                                images = Array.Empty<string>(),
                                tags = new[] { "sample" }
                            }
                        }
                    },
                    JsonOptions));

            var service = CreatePluginStoreService(root);

            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

            Assert.Empty(snapshot.CommunityPlugins);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task InstallCommunityPlugin_WithWrongChecksum_IsRejected()
    {
        var root = CreateTempRoot();

        try
        {
            var storeRoot = Path.Combine(root, "plugin-store");
            Directory.CreateDirectory(storeRoot);
            Directory.CreateDirectory(Path.Combine(storeRoot, "packages"));

            var zipPath = Path.Combine(storeRoot, "packages", "sample-plugin.zip");
            CreateSamplePluginZip(zipPath);
            WriteCatalog(
                storeRoot,
                "sample-plugin",
                "Sample Plugin",
                "1.2.3",
                "./packages/sample-plugin.zip",
                new string('0', 64));

            var service = CreatePluginStoreService(root);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.InstallCommunityPluginAsync("sample-plugin", CancellationToken.None));
            Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(storeRoot, "community", "sample-plugin")));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task PluginSdkSettings_RequireStoragePermission()
    {
        var root = CreateTempRoot();

        try
        {
            var service = await CreateInstalledSamplePluginStoreAsync(root, ["frontend"]);
            using var settings = JsonDocument.Parse("""{"baseUrl":"http://homeassistant.local"}""");

            var exception = Assert.Throws<InvalidOperationException>(
                () => service.SetPluginSdkSettings("sample-plugin", settings.RootElement));

            Assert.Contains("storage", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task PluginSdkSettingsAndSecrets_ArePersistedWithoutExposingSecret()
    {
        var root = CreateTempRoot();

        try
        {
            var service = await CreateInstalledSamplePluginStoreAsync(
                root,
                ["frontend", "storage", "secrets"]);
            using var settings = JsonDocument.Parse("""{"baseUrl":"http://homeassistant.local","refreshSeconds":10}""");

            var savedSettings = service.SetPluginSdkSettings("sample-plugin", settings.RootElement);
            var secrets = service.SetPluginSdkSecret("sample-plugin", "accessToken", "super-secret-token");
            var state = service.GetPluginSdkState("sample-plugin");

            Assert.Equal("http://homeassistant.local", savedSettings.Settings.GetProperty("baseUrl").GetString());
            Assert.True(secrets.Secrets["accesstoken"]);
            Assert.True(state.Secrets["accesstoken"]);
            Assert.Equal(10, state.Settings.GetProperty("refreshSeconds").GetInt32());

            var secretsFile = Path.Combine(root, "plugin-store", "sdk-data", "sample-plugin", "secrets.json");
            Assert.DoesNotContain("super-secret-token", File.ReadAllText(secretsFile));

            var clearedSecrets = service.ClearPluginSdkSecret("sample-plugin", "accessToken");
            Assert.False(clearedSecrets.Secrets.ContainsKey("accesstoken"));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task PluginSdkNetworkRequest_UsesStoredAuthorizationSecret()
    {
        var root = CreateTempRoot();
        var handler = new CapturingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
        });

        try
        {
            var service = await CreateInstalledSamplePluginStoreAsync(
                root,
                ["frontend", "secrets", "network"],
                new HttpClient(handler));
            service.SetPluginSdkSecret("sample-plugin", "accessToken", "ha-token");
            var body = JsonSerializer.SerializeToElement(new { entity_id = "light.office" }, JsonOptions);

            var response = await service.SendPluginSdkNetworkRequestAsync(
                "sample-plugin",
                new PluginSdkNetworkRequest(
                    "POST",
                    "https://homeassistant.local/api/services/light/turn_on",
                    new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                    body,
                    "accessToken",
                    null),
                CancellationToken.None);

            Assert.True(response.Ok);
            Assert.Equal(200, response.StatusCode);
            Assert.Equal(HttpMethod.Post, handler.Method);
            Assert.Equal("https://homeassistant.local/api/services/light/turn_on", handler.RequestUri?.ToString());
            Assert.Equal("Bearer", handler.AuthorizationScheme);
            Assert.Equal("ha-token", handler.AuthorizationParameter);
            Assert.Contains("light.office", handler.Body);
            Assert.Equal("""{"ok":true}""", response.BodyText);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task CommunityRuntimeState_ListsInstalledPluginAndEntryPointFile()
    {
        var root = CreateTempRoot();

        try
        {
            var service = await CreateInstalledSamplePluginStoreAsync(root, ["frontend"]);

            var runtimeState = service.GetCommunityRuntimeState();

            var plugin = Assert.Single(runtimeState.Plugins);
            Assert.Equal("sample-plugin", plugin.Id);
            Assert.Equal("Sample Plugin", plugin.Title);
            Assert.Equal("1.2.3", plugin.Version);
            Assert.Equal("dist/index.js", plugin.EntryPoint);
            Assert.Contains("api/plugin-store/community/sample-plugin/files/dist/index.js", plugin.ScriptUrl);

            Assert.True(service.TryGetCommunityPluginFile(
                "sample-plugin",
                "dist/index.js",
                out var filePath,
                out var contentType));
            Assert.True(File.Exists(filePath));
            Assert.Contains("javascript", contentType, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void OverlayState_CanBeOpenedAndClosed()
    {
        var root = CreateTempRoot();

        try
        {
            var service = CreatePluginStoreService(root);

            Assert.False(service.GetOverlayState().IsOpen);
            Assert.True(service.SetOverlayOpen(true).IsOpen);
            Assert.False(service.SetOverlayOpen(false).IsOpen);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void OverlayInputQueue_ReturnsInputsAfterCursorAndClearsOnClose()
    {
        var root = CreateTempRoot();

        try
        {
            var service = CreatePluginStoreService(root);

            var first = service.AddOverlayInput("down", "test");
            var second = service.AddOverlayInput("a", "test");

            var allInputs = service.GetOverlayInputs(0);
            Assert.Equal(second.Nonce, allInputs.LatestNonce);
            Assert.Equal(new[] { "down", "a" }, allInputs.Inputs.Select(input => input.Action));

            var inputsAfterFirst = service.GetOverlayInputs(first.Nonce);
            Assert.Single(inputsAfterFirst.Inputs);
            Assert.Equal(second, inputsAfterFirst.Inputs[0]);

            service.SetOverlayOpen(false);
            Assert.Empty(service.GetOverlayInputs(0).Inputs);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static PluginStoreService CreatePluginStoreService(string root, HttpClient? httpClient = null)
    {
        return new PluginStoreService(
            httpClient ?? new HttpClient(),
            CreateSettingsService(Path.Combine(root, "settings.json")),
            Path.Combine(root, "plugin-store"));
    }

    private static async Task<PluginStoreService> CreateInstalledSamplePluginStoreAsync(
        string root,
        string[] permissions,
        HttpClient? httpClient = null)
    {
        var storeRoot = Path.Combine(root, "plugin-store");
        Directory.CreateDirectory(storeRoot);
        Directory.CreateDirectory(Path.Combine(storeRoot, "packages"));

        var zipPath = Path.Combine(storeRoot, "packages", "sample-plugin.zip");
        CreateSamplePluginZip(zipPath, permissions: permissions);
        WriteCatalog(
            storeRoot,
            "sample-plugin",
            "Sample Plugin",
            "1.2.3",
            "./packages/sample-plugin.zip",
            ComputeSha256(zipPath));

        var service = CreatePluginStoreService(root, httpClient);
        await service.InstallCommunityPluginAsync("sample-plugin", CancellationToken.None);
        return service;
    }

    private static SteamLoaderSettingsService CreateSettingsService(string settingsPath)
    {
        return new SteamLoaderSettingsService(
            new WindowsAutostartService("ToolsForSteamPluginStoreTests"),
            new WindowsShellService(),
            executablePath: @"C:\ToolsForSteam\ToolsForSteam.exe",
            shellLaunchArguments: "--shell",
            settingsPath: settingsPath);
    }

    private static void CreateSamplePluginZip(
        string zipPath,
        bool includeManifest = true,
        string pluginId = "sample-plugin",
        string version = "1.2.3",
        string[]? permissions = null)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        if (includeManifest)
        {
            var manifestEntry = archive.CreateEntry("tfs-plugin.json");
            using var writer = new StreamWriter(manifestEntry.Open());
            writer.Write(JsonSerializer.Serialize(
                new
                {
                    id = pluginId,
                    name = "Sample Plugin",
                    version,
                    sdkVersion = "1.0.0",
                    entryPoint = "dist/index.js",
                    permissions = permissions ?? new[] { "frontend" }
                },
                JsonOptions));
        }

        var bundleEntry = archive.CreateEntry("dist/index.js");
        using var bundleWriter = new StreamWriter(bundleEntry.Open());
        bundleWriter.Write("console.log('sample');");
    }

    private static void WriteCatalog(
        string storeRoot,
        string id,
        string title,
        string version,
        string packagePath,
        string packageSha256 = "")
    {
        File.WriteAllText(
            Path.Combine(storeRoot, "catalog.json"),
            JsonSerializer.Serialize(
                new
                {
                    title = "TFS Community",
                    description = "Test catalog",
                    plugins = new[]
                    {
                        new
                        {
                            id,
                            title,
                            description = "Community sample",
                            author = "Test Suite",
                            category = "Utility",
                            version,
                            packagePath,
                            packageSha256,
                            images = new[] { "api/plugin-store/images/catalog/sample-plugin.png" },
                            tags = new[] { "sample" }
                        }
                    }
                },
                JsonOptions));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamloader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responseFactory;

        public CapturingHandler(Func<HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return _responseFactory();
        }
    }
}
