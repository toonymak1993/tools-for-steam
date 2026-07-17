using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SteamLoader.App.Infrastructure.PluginStore;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Infrastructure.Steam;
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
            var service = CreatePluginStoreService(root, enableCommunityCatalogBootstrap: false);

            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

            Assert.False(snapshot.CommunityCatalogAvailable);
            Assert.NotEmpty(snapshot.BuiltInPlugins);
            var smartHome = Assert.Single(snapshot.BuiltInPlugins, plugin => plugin.Id == "smart-home");
            Assert.NotEmpty(smartHome.Images);
            var discord = Assert.Single(snapshot.BuiltInPlugins, plugin => plugin.Id == "discord");
            Assert.False(discord.IsEnabled);
            Assert.NotEmpty(discord.Images);
            Assert.Empty(snapshot.CommunityPlugins);
            Assert.True(service.TryGetBuiltInImage("smart-home", out var imagePath, out var contentType));
            Assert.True(File.Exists(imagePath));
            Assert.Equal("image/svg+xml", contentType);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_CustomCatalog_IsMarkedAsUnreviewed()
    {
        var root = CreateTempRoot();

        try
        {
            var storeRoot = Path.Combine(root, "plugin-store");
            Directory.CreateDirectory(storeRoot);
            File.WriteAllText(Path.Combine(storeRoot, "catalog-source.json"), "{\"catalogUrl\":\"https://example.test/catalog.json\"}");

            var snapshot = await CreatePluginStoreService(root).GetSnapshotAsync(CancellationToken.None);

            Assert.True(snapshot.IsCustomCatalog);
            Assert.Contains("not reviewed", snapshot.CatalogTrustText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task RefreshAsync_LocalDeveloperCatalog_DoesNotOverwriteWithRemoteCatalog()
    {
        var root = CreateTempRoot();
        var handler = new CapturingHandler(() => throw new InvalidOperationException("Remote catalog must not be requested."));

        try
        {
            var storeRoot = Path.Combine(root, "plugin-store");
            Directory.CreateDirectory(storeRoot);
            WriteCatalog(
                storeRoot,
                "local-plugin",
                "Local Plugin",
                "1.0.0",
                "./packages/local-plugin.zip",
                new string('A', 64),
                permissions: ["frontend"]);
            File.WriteAllText(Path.Combine(storeRoot, "catalog-source.json"), "{\"localDevelopment\":true}");

            var snapshot = await CreatePluginStoreService(root, new HttpClient(handler)).RefreshAsync(CancellationToken.None);

            Assert.Single(snapshot.CommunityPlugins);
            Assert.True(snapshot.IsCustomCatalog);
            Assert.Null(handler.RequestUri);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_WithoutCatalog_AutoDownloadsCommunityCatalogWhenEnabled()
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
            var service = CreatePluginStoreService(root, new HttpClient(handler), enableCommunityCatalogBootstrap: true);

            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

            Assert.True(snapshot.CommunityCatalogAvailable);
            Assert.Single(snapshot.CommunityPlugins);
            Assert.Contains("tfs-plugin-database/main/catalog.json", handler.RequestUri?.ToString());
            Assert.True(File.Exists(Path.Combine(root, "plugin-store", "catalog.json")));
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
                "./packages/broken-plugin.zip",
                ComputeSha256(zipPath));

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
    public async Task GetSnapshotAsync_CommunityPluginsWithoutImages_AreStillListed()
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

            var plugin = Assert.Single(snapshot.CommunityPlugins);
            Assert.Equal("sample-plugin", plugin.Id);
            Assert.Empty(plugin.Images);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task InstallCommunityPlugin_WithoutChecksum_IsRejectedAndNotInstallable()
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
                "./packages/sample-plugin.zip");

            var service = CreatePluginStoreService(root);

            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);
            var plugin = Assert.Single(snapshot.CommunityPlugins);
            Assert.False(plugin.CanInstall);
            Assert.Contains("checksum", plugin.StatusText, StringComparison.OrdinalIgnoreCase);

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
    public async Task InstallCommunityPlugin_WithUnsupportedPermission_IsRejected()
    {
        var root = CreateTempRoot();

        try
        {
            var storeRoot = Path.Combine(root, "plugin-store");
            Directory.CreateDirectory(storeRoot);
            Directory.CreateDirectory(Path.Combine(storeRoot, "packages"));

            var zipPath = Path.Combine(storeRoot, "packages", "sample-plugin.zip");
            CreateSamplePluginZip(zipPath, permissions: ["frontend", "system"]);
            WriteCatalog(
                storeRoot,
                "sample-plugin",
                "Sample Plugin",
                "1.2.3",
                "./packages/sample-plugin.zip",
                ComputeSha256(zipPath));

            var service = CreatePluginStoreService(root);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.InstallCommunityPluginAsync("sample-plugin", CancellationToken.None));
            Assert.Contains("unsupported permission", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(storeRoot, "community", "sample-plugin")));
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
    public async Task CommunityCatalog_ExposesPermissionsBeforeInstallAndMatchesManifest()
    {
        var root = CreateTempRoot();

        try
        {
            var storeRoot = Path.Combine(root, "plugin-store");
            Directory.CreateDirectory(storeRoot);
            Directory.CreateDirectory(Path.Combine(storeRoot, "packages"));
            var zipPath = Path.Combine(storeRoot, "packages", "sample-plugin.zip");
            CreateSamplePluginZip(zipPath, permissions: ["frontend", "files", "notifications"]);
            WriteCatalog(
                storeRoot,
                "sample-plugin",
                "Sample Plugin",
                "1.2.3",
                "./packages/sample-plugin.zip",
                ComputeSha256(zipPath),
                ["frontend", "files", "notifications"],
                "1.0.0");

            var service = CreatePluginStoreService(root);
            var beforeInstall = await service.GetSnapshotAsync(CancellationToken.None);
            var plugin = Assert.Single(beforeInstall.CommunityPlugins);
            Assert.Contains("files", plugin.Permissions);
            Assert.Contains("notifications", plugin.Permissions);
            Assert.Empty(plugin.InstalledPermissions);

            var afterInstall = await service.InstallCommunityPluginAsync("sample-plugin", CancellationToken.None);
            Assert.Contains("notifications", Assert.Single(afterInstall.CommunityPlugins).InstalledPermissions);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task InstallCommunityPlugin_WhenCatalogPermissionsDiffer_IsRejected()
    {
        var root = CreateTempRoot();

        try
        {
            var storeRoot = Path.Combine(root, "plugin-store");
            Directory.CreateDirectory(storeRoot);
            Directory.CreateDirectory(Path.Combine(storeRoot, "packages"));
            var zipPath = Path.Combine(storeRoot, "packages", "sample-plugin.zip");
            CreateSamplePluginZip(zipPath, permissions: ["frontend", "network"]);
            WriteCatalog(
                storeRoot,
                "sample-plugin",
                "Sample Plugin",
                "1.2.3",
                "./packages/sample-plugin.zip",
                ComputeSha256(zipPath),
                ["frontend"],
                "1.0.0");

            var service = CreatePluginStoreService(root);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.InstallCommunityPluginAsync("sample-plugin", CancellationToken.None));
            Assert.Contains("permissions", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task InstallCommunityPlugin_WhenNetworkHostsAreMissing_IsRejected()
    {
        var root = CreateTempRoot();

        try
        {
            var storeRoot = Path.Combine(root, "plugin-store");
            Directory.CreateDirectory(storeRoot);
            Directory.CreateDirectory(Path.Combine(storeRoot, "packages"));
            var zipPath = Path.Combine(storeRoot, "packages", "sample-plugin.zip");
            CreateSamplePluginZip(zipPath, permissions: ["frontend", "network"]);
            WriteCatalog(
                storeRoot,
                "sample-plugin",
                "Sample Plugin",
                "1.2.3",
                "./packages/sample-plugin.zip",
                ComputeSha256(zipPath),
                ["frontend", "network"],
                "1.0.0");

            var service = CreatePluginStoreService(root);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.InstallCommunityPluginAsync("sample-plugin", CancellationToken.None));
            Assert.Contains("networkHosts", exception.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task PluginSdkFiles_RequireFilesPermission()
    {
        var root = CreateTempRoot();

        try
        {
            var service = await CreateInstalledSamplePluginStoreAsync(root, ["frontend"]);

            var exception = Assert.Throws<InvalidOperationException>(
                () => service.ListPluginSdkFiles(
                    "sample-plugin",
                    new PluginSdkFileListRequest("", false)));

            Assert.Contains("files", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task PluginSdkFiles_SupportSandboxedFileLifecycle()
    {
        var root = CreateTempRoot();

        try
        {
            var service = await CreateInstalledSamplePluginStoreAsync(root, ["frontend", "files"]);

            service.CreatePluginSdkDirectory(
                "sample-plugin",
                new PluginSdkFilePathRequest("cache/nested", true));
            service.WritePluginSdkFile(
                "sample-plugin",
                new PluginSdkFileWriteRequest("cache/nested/state.txt", "hello", "utf8", false, true));
            service.WritePluginSdkFile(
                "sample-plugin",
                new PluginSdkFileWriteRequest("cache/nested/state.txt", " world", "utf8", true, true));

            var content = service.ReadPluginSdkFile(
                "sample-plugin",
                new PluginSdkFileReadRequest("cache/nested/state.txt", "utf8"));
            Assert.Equal("hello world", content.Content);
            Assert.Equal(11, content.Size);

            var listing = service.ListPluginSdkFiles(
                "sample-plugin",
                new PluginSdkFileListRequest("cache", true));
            Assert.Contains(listing.Entries, entry => entry.Path == "cache/nested/state.txt" && !entry.IsDirectory);
            Assert.Equal(11, listing.UsedBytes);

            service.CopyPluginSdkFile(
                "sample-plugin",
                new PluginSdkFileTransferRequest("cache/nested/state.txt", "copy.txt", false));
            service.MovePluginSdkFile(
                "sample-plugin",
                new PluginSdkFileTransferRequest("copy.txt", "moved.txt", false));
            var moved = service.GetPluginSdkFileInfo(
                "sample-plugin",
                new PluginSdkFilePathRequest("moved.txt", false));
            Assert.True(moved.Exists);
            Assert.Equal(11, moved.Size);

            var deleted = service.DeletePluginSdkFile(
                "sample-plugin",
                new PluginSdkFilePathRequest("cache", true));
            Assert.False(deleted.Exists);
            Assert.Equal(11, deleted.UsedBytes);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task PluginSdkFiles_RejectPathTraversal()
    {
        var root = CreateTempRoot();

        try
        {
            var service = await CreateInstalledSamplePluginStoreAsync(root, ["frontend", "files"]);

            var exception = Assert.Throws<InvalidOperationException>(
                () => service.WritePluginSdkFile(
                    "sample-plugin",
                    new PluginSdkFileWriteRequest("../outside.txt", "blocked", "utf8", false, true)));

            Assert.Contains("safe relative path", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, "plugin-store", "sdk-data", "outside.txt")));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task PluginSdkNotifications_RequirePermissionAndApplyRateLimit()
    {
        var root = CreateTempRoot();

        try
        {
            var serviceWithoutPermission = await CreateInstalledSamplePluginStoreAsync(
                Path.Combine(root, "without-permission"),
                ["frontend"]);
            Assert.Throws<InvalidOperationException>(() => serviceWithoutPermission.CreatePluginSdkNotification(
                "sample-plugin",
                new PluginSdkNotificationRequest("Ready", "Plugin loaded.", "success", 3000)));

            var service = await CreateInstalledSamplePluginStoreAsync(
                Path.Combine(root, "with-permission"),
                ["frontend", "notifications"]);
            for (var index = 0; index < 5; index++)
            {
                var notification = service.CreatePluginSdkNotification(
                    "sample-plugin",
                    new PluginSdkNotificationRequest("Ready", $"Message {index}", "success", 3000));
                Assert.Equal("success", notification.Level);
                Assert.Equal("sample-plugin", notification.PluginId);
            }

            var exception = Assert.Throws<InvalidOperationException>(() => service.CreatePluginSdkNotification(
                "sample-plugin",
                new PluginSdkNotificationRequest("Too many", "Rate limited", "info", 3000)));
            Assert.Contains("rate limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task PluginSdkLogging_WritesBoundedStructuredLog()
    {
        var root = CreateTempRoot();

        try
        {
            var service = await CreateInstalledSamplePluginStoreAsync(root, ["frontend", "logging"]);
            var data = JsonSerializer.SerializeToElement(new { entityCount = 4 }, JsonOptions);

            var result = service.WritePluginSdkLog(
                "sample-plugin",
                new PluginSdkLogRequest("warning", "Refresh took longer than expected.", data));

            Assert.Equal("warning", result.Level);
            Assert.True(result.LogSize > 0);
            var logPath = Path.Combine(
                root,
                "plugin-store",
                "sdk-data",
                "sample-plugin",
                "logs",
                "plugin.log");
            var logText = File.ReadAllText(logPath);
            Assert.Contains("Refresh took longer", logText);
            Assert.Contains("entityCount", logText);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task PluginSdkNativeCapabilities_RequireExactDeclaredPermission()
    {
        var root = CreateTempRoot();

        try
        {
            var deniedService = await CreateInstalledSamplePluginStoreAsync(
                Path.Combine(root, "denied"),
                ["frontend"]);
            var exception = Assert.Throws<InvalidOperationException>(() =>
                deniedService.EnsurePluginSdkPermission("sample-plugin", "native.audio"));
            Assert.Contains("native.audio", exception.Message, StringComparison.OrdinalIgnoreCase);

            var allowedService = await CreateInstalledSamplePluginStoreAsync(
                Path.Combine(root, "allowed"),
                [
                    "frontend",
                    "native.audio",
                    "native.processes",
                    "native.display",
                    "native.themes",
                    "native.artwork",
                    "native.app-start",
                    "native.store-sync",
                    "native.automation",
                    "native.performance",
                    "native.power",
                    "native.full-trust"
                ]);

            foreach (var permission in new[]
            {
                "native.audio",
                "native.processes",
                "native.display",
                "native.themes",
                "native.artwork",
                "native.app-start",
                "native.store-sync",
                "native.automation",
                "native.performance",
                "native.power",
                "native.full-trust"
            })
            {
                allowedService.EnsurePluginSdkPermission("sample-plugin", permission);
            }
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task FullTrustRuntime_RunsProcessesAndUsesArbitraryFilesystemBridge()
    {
        var root = CreateTempRoot();

        try
        {
            var service = await CreateInstalledSamplePluginStoreAsync(
                root,
                ["frontend", "native.full-trust"]);
            service.EnsurePluginSdkPermission("sample-plugin", "native.full-trust");
            await using var runtime = new PluginFullTrustRuntime(
                service,
                new SteamDevToolsClient(new HttpClient(), new Uri("http://127.0.0.1:1")));

            var runResult = await runtime.ExecuteSystemAsync(
                "sample-plugin",
                "run",
                JsonSerializer.SerializeToElement(new
                {
                    fileName = "cmd.exe",
                    arguments = new[] { "/d", "/c", "echo", "sdk-full-trust" },
                    timeoutMs = 10_000
                }),
                CancellationToken.None);
            var runJson = JsonSerializer.SerializeToElement(runResult, JsonOptions);
            Assert.True(runJson.GetProperty("success").GetBoolean());
            Assert.Contains("sdk-full-trust", runJson.GetProperty("output").GetString(), StringComparison.OrdinalIgnoreCase);

            await runtime.ExecuteFileSystemAsync(
                "sample-plugin",
                "writeText",
                JsonSerializer.SerializeToElement(new { path = "runtime/test.txt", content = "full trust" }),
                CancellationToken.None);
            var readResult = await runtime.ExecuteFileSystemAsync(
                "sample-plugin",
                "readText",
                JsonSerializer.SerializeToElement(new { path = "runtime/test.txt" }),
                CancellationToken.None);
            var readJson = JsonSerializer.SerializeToElement(readResult, JsonOptions);
            Assert.Equal("full trust", readJson.GetProperty("content").GetString());

            var backendDirectory = Path.Combine(service.GetPluginInstallationDirectory("sample-plugin"), "backend");
            Directory.CreateDirectory(backendDirectory);
            File.WriteAllText(
                Path.Combine(backendDirectory, "rpc.ps1"),
                "while ($null -ne ($line = [Console]::In.ReadLine())) { " +
                "$request = $line | ConvertFrom-Json; " +
                "@{ tfsRpcId = $request.tfsRpcId; result = @{ message = 'pong' } } | " +
                "ConvertTo-Json -Depth 10 -Compress | Write-Output }");
            var backendResult = await runtime.ExecuteSystemAsync(
                "sample-plugin",
                "startBackend",
                JsonSerializer.SerializeToElement(new
                {
                    entryPoint = "backend/rpc.ps1",
                    runtime = "powershell",
                    arguments = Array.Empty<string>()
                }),
                CancellationToken.None);
            var backendJson = JsonSerializer.SerializeToElement(backendResult, JsonOptions);
            var managedProcessId = backendJson.GetProperty("processId").GetString();
            var rpcResult = await runtime.ExecuteSystemAsync(
                "sample-plugin",
                "call",
                JsonSerializer.SerializeToElement(new
                {
                    processId = managedProcessId,
                    method = "ping",
                    arguments = new { value = 1 },
                    timeoutMs = 10_000
                }),
                CancellationToken.None);
            var rpcJson = JsonSerializer.SerializeToElement(rpcResult, JsonOptions);
            Assert.Equal("pong", rpcJson.GetProperty("message").GetString());
            runtime.StopAll("sample-plugin");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task InstallCommunityPlugin_WithBundledFullTrustBackend_IsAccepted()
    {
        var root = CreateTempRoot();

        try
        {
            var storeRoot = Path.Combine(root, "plugin-store");
            Directory.CreateDirectory(storeRoot);
            Directory.CreateDirectory(Path.Combine(storeRoot, "packages"));
            var zipPath = Path.Combine(storeRoot, "packages", "backend-plugin.zip");
            CreateSamplePluginZip(
                zipPath,
                pluginId: "backend-plugin",
                version: "1.0.0",
                permissions: ["frontend", "native.full-trust"],
                includeBackend: true);
            WriteCatalog(
                storeRoot,
                "backend-plugin",
                "Backend Plugin",
                "1.0.0",
                "./packages/backend-plugin.zip",
                ComputeSha256(zipPath),
                permissions: ["frontend", "native.full-trust"],
                sdkVersion: "1.0.0");

            var snapshot = await CreatePluginStoreService(root).InstallCommunityPluginAsync(
                "backend-plugin",
                CancellationToken.None);

            Assert.True(Assert.Single(snapshot.CommunityPlugins).IsInstalled);
            Assert.True(File.Exists(Path.Combine(
                root,
                "plugin-store",
                "community",
                "backend-plugin",
                "backend",
                "plugin.ps1")));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task FullTrustRuntime_ListsSteamDevToolsTargetsWithoutExposingDebuggerSocket()
    {
        var root = CreateTempRoot();

        try
        {
            var service = await CreateInstalledSamplePluginStoreAsync(
                root,
                ["frontend", "native.full-trust"]);
            var handler = new CapturingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"id\":\"target-1\",\"title\":\"Big-Picture\",\"type\":\"page\",\"url\":\"https://steamloopback.host/index.html\",\"webSocketDebuggerUrl\":\"ws://127.0.0.1/private\"}]",
                    Encoding.UTF8,
                    "application/json")
            });
            await using var runtime = new PluginFullTrustRuntime(
                service,
                new SteamDevToolsClient(new HttpClient(handler), new Uri("http://127.0.0.1:8080")));

            var result = await runtime.ExecuteSteamAsync(
                "sample-plugin",
                "targets",
                null,
                CancellationToken.None);
            var json = JsonSerializer.SerializeToElement(result, JsonOptions);

            var target = Assert.Single(json.EnumerateArray());
            Assert.Equal("target-1", target.GetProperty("id").GetString());
            Assert.Equal("Big-Picture", target.GetProperty("title").GetString());
            Assert.False(target.TryGetProperty("webSocketDebuggerUrl", out _));
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
    public async Task PluginSdkNetworkRequest_BlocksHostsOutsideManifestAllowlist()
    {
        var root = CreateTempRoot();
        var handler = new CapturingHandler(() => new HttpResponseMessage(HttpStatusCode.OK));

        try
        {
            var service = await CreateInstalledSamplePluginStoreAsync(
                root,
                ["frontend", "network"],
                new HttpClient(handler));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SendPluginSdkNetworkRequestAsync(
                    "sample-plugin",
                    new PluginSdkNetworkRequest("GET", "https://example.com/data", null, null, null, null),
                    CancellationToken.None));

            Assert.Contains("networkHosts", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(handler.RequestUri);
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
            var third = service.AddOverlayInput("search-back", "test");

            var allInputs = service.GetOverlayInputs(0);
            Assert.Equal(third.Nonce, allInputs.LatestNonce);
            Assert.Equal(new[] { "down", "a", "search-back" }, allInputs.Inputs.Select(input => input.Action));

            var inputsAfterFirst = service.GetOverlayInputs(first.Nonce);
            Assert.Equal(new[] { second, third }, inputsAfterFirst.Inputs);

            service.SetOverlayOpen(false);
            Assert.Empty(service.GetOverlayInputs(0).Inputs);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static PluginStoreService CreatePluginStoreService(
        string root,
        HttpClient? httpClient = null,
        bool enableCommunityCatalogBootstrap = false)
    {
        var client = httpClient ?? new HttpClient();
        return new PluginStoreService(
            client,
            CreateSettingsService(Path.Combine(root, "settings.json")),
            Path.Combine(root, "plugin-store"),
            enableCommunityCatalogBootstrap,
            client);
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
        var networkHosts = permissions.Contains("network", StringComparer.OrdinalIgnoreCase)
            ? new[] { "<local>" }
            : Array.Empty<string>();
        CreateSamplePluginZip(zipPath, permissions: permissions, networkHosts: networkHosts);
        WriteCatalog(
            storeRoot,
            "sample-plugin",
            "Sample Plugin",
            "1.2.3",
            "./packages/sample-plugin.zip",
            ComputeSha256(zipPath),
            networkHosts: networkHosts);

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
        string[]? permissions = null,
        string[]? networkHosts = null,
        bool includeBackend = false)
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
                    permissions = permissions ?? new[] { "frontend" },
                    networkHosts = networkHosts ?? Array.Empty<string>(),
                    backend = includeBackend
                        ? new
                        {
                            entryPoint = "backend/plugin.ps1",
                            runtime = "powershell",
                            autoStart = true
                        }
                        : null
                },
                JsonOptions));
        }

        var bundleEntry = archive.CreateEntry("dist/index.js");
        using (var bundleWriter = new StreamWriter(bundleEntry.Open()))
        {
            bundleWriter.Write("console.log('sample');");
        }

        if (includeBackend)
        {
            var backendEntry = archive.CreateEntry("backend/plugin.ps1");
            using var backendWriter = new StreamWriter(backendEntry.Open());
            backendWriter.Write("Write-Output 'backend ready'");
        }
    }

    private static void WriteCatalog(
        string storeRoot,
        string id,
        string title,
        string version,
        string packagePath,
        string packageSha256 = "",
        string[]? permissions = null,
        string sdkVersion = "",
        string[]? networkHosts = null)
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
                            sdkVersion,
                            permissions = permissions ?? Array.Empty<string>(),
                            networkHosts = networkHosts ?? Array.Empty<string>(),
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
