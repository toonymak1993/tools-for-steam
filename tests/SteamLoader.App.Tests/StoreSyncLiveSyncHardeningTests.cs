using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SteamLoader.App.Hosting;
using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Models;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class StoreSyncLiveSyncHardeningTests
{
    [Fact]
    public void TryBuildLiveShortcutMirrorEntries_AppendsCreatedShortcutWithLiveAppId()
    {
        var serviceType = typeof(StoreSyncService);
        var storeDefinitionType = serviceType.GetNestedType("StoreDefinition", BindingFlags.NonPublic);
        var storeGameEntryType = serviceType.GetNestedType("StoreGameEntry", BindingFlags.NonPublic);
        var actionKindType = serviceType.GetNestedType("StoreSyncActionKind", BindingFlags.NonPublic);
        var analysisItemType = serviceType.GetNestedType("StoreSyncAnalysisItem", BindingFlags.NonPublic);
        var analysisType = serviceType.GetNestedType("StoreSyncAnalysis", BindingFlags.NonPublic);
        var method = serviceType.GetMethod("TryBuildLiveShortcutMirrorEntries", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(storeDefinitionType);
        Assert.NotNull(storeGameEntryType);
        Assert.NotNull(actionKindType);
        Assert.NotNull(analysisItemType);
        Assert.NotNull(analysisType);
        Assert.NotNull(method);

        var definition = CreateNonPublicInstance(
            storeDefinitionType!,
            "xbox-game-pass",
            "Xbox / Game Pass",
            "Test store",
            true,
            false);
        var game = CreateNonPublicInstance(
            storeGameEntryType!,
            "xbox-game-pass",
            @"xbox|d:\xboxgames\quake\content\microsoftgame.config|d:\xboxgames\quake",
            "QUAKE",
            @"D:\XboxGames\QUAKE\Content\bastet_WinStore.exe",
            @"D:\XboxGames\QUAKE\Content",
            string.Empty);
        var actionKind = Enum.Parse(actionKindType!, "Create");
        var item = CreateNonPublicInstance(
            analysisItemType!,
            "xbox-game-pass-02ed64b6991a",
            "xbox-game-pass-02ed64b6991a",
            definition,
            game,
            new StoreSyncTitleOverride(),
            "QUAKE",
            "QUAKE",
            1111u,
            actionKind,
            "Create",
            string.Empty,
            null,
            null,
            null,
            Array.Empty<string>());
        var analysisItems = Array.CreateInstance(analysisItemType!, 1);
        analysisItems.SetValue(item, 0);
        var emptyCleanupCandidates = Array.CreateInstance(
            serviceType.GetNestedType("StoreSyncCleanupCandidate", BindingFlags.NonPublic)!,
            0);
        var preview = new StoreSyncPreviewState(1, 0, 0, 0, 0, 0, 0, []);
        var analysis = CreateNonPublicInstance(
            analysisType!,
            analysisItems,
            emptyCleanupCandidates,
            0,
            preview);

        var arguments = new object?[]
        {
            new List<Dictionary<string, object?>>(),
            analysis,
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
            {
                ["xbox-game-pass-02ed64b6991a"] = 3155387848u,
            },
            null,
        };

        var changed = (bool?)method!.Invoke(null, arguments);

        Assert.True(changed);

        var mirroredEntries = Assert.IsType<List<Dictionary<string, object?>>>(arguments[3]);
        var shortcutEntry = Assert.Single(mirroredEntries);

        Assert.Equal("QUAKE", shortcutEntry["appname"]);
        Assert.Equal("steamloader://managed", shortcutEntry["ShortcutPath"]);
        Assert.Equal("xbox-game-pass", ((Dictionary<string, object?>)shortcutEntry["tags"]!)["2"]);
        Assert.Equal("xbox-game-pass-02ed64b6991a", ((Dictionary<string, object?>)shortcutEntry["tags"]!)["3"]);

        var rawAppId = Assert.IsType<int>(shortcutEntry["appid"]);
        Assert.Equal(3155387848u, unchecked((uint)rawAppId));
    }

    [Fact]
    public void TryBuildLiveShortcutMirrorEntries_UpgradesXboxShortcutWithoutAppendingDuplicate()
    {
        var serviceType = typeof(StoreSyncService);
        var storeDefinitionType = serviceType.GetNestedType("StoreDefinition", BindingFlags.NonPublic);
        var storeGameEntryType = serviceType.GetNestedType("StoreGameEntry", BindingFlags.NonPublic);
        var actionKindType = serviceType.GetNestedType("StoreSyncActionKind", BindingFlags.NonPublic);
        var analysisItemType = serviceType.GetNestedType("StoreSyncAnalysisItem", BindingFlags.NonPublic);
        var analysisType = serviceType.GetNestedType("StoreSyncAnalysis", BindingFlags.NonPublic);
        var method = serviceType.GetMethod("TryBuildLiveShortcutMirrorEntries", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(storeDefinitionType);
        Assert.NotNull(storeGameEntryType);
        Assert.NotNull(actionKindType);
        Assert.NotNull(analysisItemType);
        Assert.NotNull(analysisType);
        Assert.NotNull(method);

        var existingEntry = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["appid"] = unchecked((int)3155387848u),
            ["appname"] = "QUAKE",
            ["Exe"] = "\"D:\\XboxGames\\QUAKE\\Content\\bastet_WinStore.exe\"",
            ["StartDir"] = "\"D:\\XboxGames\\QUAKE\\Content\"",
            ["icon"] = @"D:\XboxGames\QUAKE\Content\bastet_WinStore.exe",
            ["ShortcutPath"] = "steamloader://managed",
            ["LaunchOptions"] = string.Empty,
            ["IsHidden"] = 0,
            ["AllowDesktopConfig"] = 1,
            ["AllowOverlay"] = 1,
            ["OpenVR"] = 0,
            ["Devkit"] = 0,
            ["DevkitGameID"] = string.Empty,
            ["DevkitOverrideAppID"] = 0,
            ["LastPlayTime"] = 0,
            ["FlatpakAppID"] = string.Empty,
            ["tags"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["0"] = "Tools for Steam",
                ["1"] = "Store Sync",
                ["2"] = "xbox-game-pass",
                ["3"] = "xbox-game-pass-02ed64b6991a",
            },
        };

        var definition = CreateNonPublicInstance(
            storeDefinitionType!,
            "xbox-game-pass",
            "Xbox / Game Pass",
            "Test store",
            true,
            false);
        var game = CreateNonPublicInstance(
            storeGameEntryType!,
            "xbox-game-pass",
            @"xbox|d:\xboxgames\quake\content\microsoftgame.config|d:\xboxgames\quake",
            "QUAKE",
            @"D:\XboxGames\QUAKE\Content\bastet_WinStore.exe",
            @"D:\XboxGames\QUAKE\Content",
            string.Empty);
        var actionKind = Enum.Parse(actionKindType!, "Create");
        var item = CreateNonPublicInstance(
            analysisItemType!,
            "xbox-game-pass-02ed64b6991a",
            "xbox-game-pass-02ed64b6991a",
            definition,
            game,
            new StoreSyncTitleOverride(),
            "QUAKE",
            "QUAKE",
            3155387848u,
            actionKind,
            "Create",
            string.Empty,
            null,
            null,
            null,
            Array.Empty<string>());
        var analysisItems = Array.CreateInstance(analysisItemType!, 1);
        analysisItems.SetValue(item, 0);
        var emptyCleanupCandidates = Array.CreateInstance(
            serviceType.GetNestedType("StoreSyncCleanupCandidate", BindingFlags.NonPublic)!,
            0);
        var preview = new StoreSyncPreviewState(1, 0, 0, 0, 0, 0, 0, []);
        var analysis = CreateNonPublicInstance(
            analysisType!,
            analysisItems,
            emptyCleanupCandidates,
            0,
            preview);

        var arguments = new object?[]
        {
            new List<Dictionary<string, object?>> { existingEntry },
            analysis,
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
            {
                ["xbox-game-pass-02ed64b6991a"] = 3155387848u,
            },
            null,
        };

        var changed = (bool?)method!.Invoke(null, arguments);

        Assert.True(changed);

        var mirroredEntries = Assert.IsType<List<Dictionary<string, object?>>>(arguments[3]);
        var shortcutEntry = Assert.Single(mirroredEntries);
        Assert.Equal("QUAKE", shortcutEntry["appname"]);
        Assert.Equal($"\"{Path.Combine(AppContext.BaseDirectory, "ToolsForSteam.exe")}\"", shortcutEntry["Exe"]);
        Assert.StartsWith(XboxStoreLaunchHost.LaunchArgument, Assert.IsType<string>(shortcutEntry["LaunchOptions"]));
    }

    [Fact]
    public void TryBuildLiveShortcutMirrorEntries_RefreshesManagedShortcutWithoutDroppingPlaytime()
    {
        var serviceType = typeof(StoreSyncService);
        var storeDefinitionType = serviceType.GetNestedType("StoreDefinition", BindingFlags.NonPublic);
        var storeGameEntryType = serviceType.GetNestedType("StoreGameEntry", BindingFlags.NonPublic);
        var actionKindType = serviceType.GetNestedType("StoreSyncActionKind", BindingFlags.NonPublic);
        var analysisItemType = serviceType.GetNestedType("StoreSyncAnalysisItem", BindingFlags.NonPublic);
        var analysisType = serviceType.GetNestedType("StoreSyncAnalysis", BindingFlags.NonPublic);
        var existingShortcutEntryType = serviceType.GetNestedType("ExistingShortcutEntry", BindingFlags.NonPublic);
        var method = serviceType.GetMethod("TryBuildLiveShortcutMirrorEntries", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(storeDefinitionType);
        Assert.NotNull(storeGameEntryType);
        Assert.NotNull(actionKindType);
        Assert.NotNull(analysisItemType);
        Assert.NotNull(analysisType);
        Assert.NotNull(existingShortcutEntryType);
        Assert.NotNull(method);

        var existingEntry = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["appid"] = unchecked((int)1002u),
            ["appname"] = "Anno 117 Old",
            ["Exe"] = "\"C:\\Games\\Ubisoft\\UbisoftConnect.exe\"",
            ["StartDir"] = "\"C:\\Games\\Ubisoft\"",
            ["icon"] = @"C:\Games\Ubisoft\UbisoftConnect.exe",
            ["ShortcutPath"] = "steamloader://managed",
            ["LaunchOptions"] = "uplay://launch/9999/0",
            ["IsHidden"] = 0,
            ["AllowDesktopConfig"] = 1,
            ["AllowOverlay"] = 1,
            ["OpenVR"] = 0,
            ["Devkit"] = 0,
            ["DevkitGameID"] = string.Empty,
            ["DevkitOverrideAppID"] = 0,
            ["LastPlayTime"] = 77,
            ["FlatpakAppID"] = string.Empty,
            ["tags"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["0"] = "Tools for Steam",
                ["1"] = "Store Sync",
                ["2"] = "ubisoft-connect",
                ["3"] = "ubisoft-connect-anno",
            },
        };

        var definition = CreateNonPublicInstance(
            storeDefinitionType!,
            "ubisoft-connect",
            "Ubisoft Connect",
            "Test store",
            true,
            false);
        var game = CreateNonPublicInstance(
            storeGameEntryType!,
            "ubisoft-connect",
            "ubisoft|anno",
            "Anno 117 Pax Romana",
            @"C:\Games\Ubisoft\UbisoftConnect.exe",
            @"C:\Games\Ubisoft",
            "uplay://launch/1234/0");
        var existingShortcut = CreateNonPublicInstance(
            existingShortcutEntryType!,
            0,
            1002u,
            "Anno 117 Old",
            @"C:\Games\Ubisoft\UbisoftConnect.exe",
            @"C:\Games\Ubisoft",
            "uplay://launch/9999/0",
            true,
            "ubisoft-connect",
            "ubisoft-connect-anno",
            existingEntry);
        var actionKind = Enum.Parse(actionKindType!, "RefreshManaged");
        var item = CreateNonPublicInstance(
            analysisItemType!,
            "ubisoft-connect-anno",
            "ubisoft-connect-anno",
            definition,
            game,
            new StoreSyncTitleOverride(),
            "Anno 117 Pax Romana",
            "Anno 117 Pax Romana",
            1002u,
            actionKind,
            "Refresh Managed",
            string.Empty,
            existingShortcut,
            null,
            null,
            Array.Empty<string>());
        var analysisItems = Array.CreateInstance(analysisItemType!, 1);
        analysisItems.SetValue(item, 0);
        var emptyCleanupCandidates = Array.CreateInstance(
            serviceType.GetNestedType("StoreSyncCleanupCandidate", BindingFlags.NonPublic)!,
            0);
        var preview = new StoreSyncPreviewState(0, 1, 0, 0, 0, 0, 0, []);
        var analysis = CreateNonPublicInstance(
            analysisType!,
            analysisItems,
            emptyCleanupCandidates,
            0,
            preview);

        var arguments = new object?[]
        {
            new List<Dictionary<string, object?>> { existingEntry },
            analysis,
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase),
            null,
        };

        var changed = (bool?)method!.Invoke(null, arguments);

        Assert.True(changed);

        var mirroredEntries = Assert.IsType<List<Dictionary<string, object?>>>(arguments[3]);
        var refreshedEntry = Assert.Single(mirroredEntries);

        Assert.Equal("Anno 117 Pax Romana", refreshedEntry["appname"]);
        Assert.Equal("uplay://launch/1234/0", refreshedEntry["LaunchOptions"]);
        Assert.Equal(77, refreshedEntry["LastPlayTime"]);
        Assert.Equal("ubisoft-connect-anno", ((Dictionary<string, object?>)refreshedEntry["tags"]!)["3"]);
    }

    [Fact]
    public void ResolveActionKind_KeepsManagedManifestOnRefresh_WhenShortcutIsTemporarilyMissing()
    {
        var serviceType = typeof(StoreSyncService);
        var actionKindType = serviceType.GetNestedType("StoreSyncActionKind", BindingFlags.NonPublic);
        var method = serviceType.GetMethod("ResolveActionKind", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(actionKindType);
        Assert.NotNull(method);

        var configuration = new StoreSyncConfiguration();
        var manifestEntry = new StoreSyncManifestEntry
        {
            TitleId = "xbox-game-pass-doom3",
            StoreId = "xbox-game-pass",
            StoreItemId = @"xbox|c:\xboxgames\doom 3\content\microsoftgame.config|c:\xboxgames\doom 3",
            Title = "DOOM 3",
            EffectiveTitle = "DOOM 3",
            ExecutablePath = @"C:\XboxGames\DOOM 3\Content\Doom3.exe",
            AppId = 3304478195u,
            ManagedShortcut = true,
        };

        var result = method!.Invoke(null, [configuration, new StoreSyncTitleOverride(), manifestEntry, null]);

        Assert.Equal(Enum.Parse(actionKindType!, "RefreshManaged"), result);
    }

    [Fact]
    public void BuildLiveShortcutSyncPlan_RefreshesManagedShortcutByManifestAppId_WhenLoadedShortcutIsMissing()
    {
        var serviceType = typeof(StoreSyncService);
        var storeDefinitionType = serviceType.GetNestedType("StoreDefinition", BindingFlags.NonPublic);
        var storeGameEntryType = serviceType.GetNestedType("StoreGameEntry", BindingFlags.NonPublic);
        var actionKindType = serviceType.GetNestedType("StoreSyncActionKind", BindingFlags.NonPublic);
        var analysisItemType = serviceType.GetNestedType("StoreSyncAnalysisItem", BindingFlags.NonPublic);
        var analysisType = serviceType.GetNestedType("StoreSyncAnalysis", BindingFlags.NonPublic);
        var method = serviceType.GetMethod("BuildLiveShortcutSyncPlan", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(storeDefinitionType);
        Assert.NotNull(storeGameEntryType);
        Assert.NotNull(actionKindType);
        Assert.NotNull(analysisItemType);
        Assert.NotNull(analysisType);
        Assert.NotNull(method);

        var definition = CreateNonPublicInstance(
            storeDefinitionType!,
            "xbox-game-pass",
            "Xbox / Game Pass",
            "Test store",
            true,
            false);
        var game = CreateNonPublicInstance(
            storeGameEntryType!,
            "xbox-game-pass",
            @"xbox|c:\xboxgames\doom 3\content\microsoftgame.config|c:\xboxgames\doom 3",
            "DOOM 3",
            @"C:\XboxGames\DOOM 3\Content\Doom3.exe",
            @"C:\XboxGames\DOOM 3\Content",
            string.Empty);
        var manifestEntry = new StoreSyncManifestEntry
        {
            TitleId = "xbox-game-pass-doom3",
            StoreId = "xbox-game-pass",
            StoreItemId = @"xbox|c:\xboxgames\doom 3\content\microsoftgame.config|c:\xboxgames\doom 3",
            Title = "DOOM 3",
            EffectiveTitle = "DOOM 3",
            ExecutablePath = @"C:\XboxGames\DOOM 3\Content\Doom3.exe",
            AppId = 3304478195u,
            ManagedShortcut = true,
        };
        var actionKind = Enum.Parse(actionKindType!, "RefreshManaged");
        var item = CreateNonPublicInstance(
            analysisItemType!,
            "xbox-game-pass-doom3",
            "xbox-game-pass-doom3",
            definition,
            game,
            new StoreSyncTitleOverride(),
            "DOOM 3",
            "DOOM 3",
            3304478195u,
            actionKind,
            "Refresh Managed",
            string.Empty,
            null,
            manifestEntry,
            null,
            Array.Empty<string>());
        var analysisItems = Array.CreateInstance(analysisItemType!, 1);
        analysisItems.SetValue(item, 0);
        var emptyCleanupCandidates = Array.CreateInstance(
            serviceType.GetNestedType("StoreSyncCleanupCandidate", BindingFlags.NonPublic)!,
            0);
        var preview = new StoreSyncPreviewState(0, 1, 0, 0, 0, 0, 0, []);
        var analysis = CreateNonPublicInstance(
            analysisType!,
            analysisItems,
            emptyCleanupCandidates,
            0,
            preview);

        var plan = method!.Invoke(
            null,
            [
                new StoreSyncConfiguration(),
                new SteamProfileInfo("User", "user", "1", "1", @"C:\Steam\shortcuts.vdf"),
                analysis
            ]);

        Assert.NotNull(plan);

        var updateOperations = plan!.GetType().GetProperty("UpdateOperations")?.GetValue(plan) as System.Collections.IEnumerable;
        Assert.NotNull(updateOperations);

        var update = Assert.Single(updateOperations!.Cast<object>());
        var appId = (uint?)update.GetType().GetProperty("AppId")?.GetValue(update);
        Assert.Equal(3304478195u, appId);
        var forceCreate = (bool?)update.GetType().GetProperty("ForceCreate")?.GetValue(update);
        Assert.True(forceCreate);
    }

    [Fact]
    public void TryCreateXboxGameFromConfig_UsesAppxManifestDisplayName_WhenConfigOnlyHasMsResourcePlaceholder()
    {
        var serviceType = typeof(StoreSyncService);
        var method = serviceType.GetMethod("TryCreateXboxGameFromConfig", BindingFlags.NonPublic | BindingFlags.Static);
        var storeGameEntryType = serviceType.GetNestedType("StoreGameEntry", BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.NotNull(storeGameEntryType);

        var rootDirectory = Path.Combine(Path.GetTempPath(), $"tfs-xbox-title-{Guid.NewGuid():N}");
        var contentDirectory = Path.Combine(rootDirectory, "Content");
        Directory.CreateDirectory(contentDirectory);

        try
        {
            File.WriteAllText(
                Path.Combine(contentDirectory, "MicrosoftGame.config"),
                """
                <?xml version="1.0" encoding="utf-8"?>
                <Game>
                  <ShellVisuals DefaultDisplayName="ms-resource:AppDisplayName" Description="ms-resource:AppDescription" />
                  <Executables>
                    <Executable Name="Kani.exe" TargetDeviceFamily="PC" />
                  </Executables>
                </Game>
                """);
            File.WriteAllText(
                Path.Combine(contentDirectory, "appxmanifest.xml"),
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <Package xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                  <Identity Name="tinyBuildGames.KillItWithFire2" Publisher="CN=Test" Version="1.0.0.0" ProcessorArchitecture="x64" />
                  <Properties>
                    <DisplayName>Kill It With Fire 2</DisplayName>
                  </Properties>
                  <Applications>
                    <Application Id="App" Executable="Kani.exe" EntryPoint="Windows.FullTrustApplication">
                      <uap:VisualElements DisplayName="ms-resource:AppDisplayName" />
                    </Application>
                  </Applications>
                </Package>
                """);
            File.WriteAllBytes(Path.Combine(contentDirectory, "Kani.exe"), [0x4D, 0x5A]);

            var arguments = new object?[]
            {
                rootDirectory,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                null,
            };

            var created = (bool?)method!.Invoke(null, arguments);

            Assert.True(created);
            Assert.NotNull(arguments[2]);

            var title = (string?)storeGameEntryType!.GetProperty("Title")?.GetValue(arguments[2]);
            Assert.Equal("Kill It With Fire 2", title);
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void TryFindExistingShortcut_PrefersMatchingLaunchOptions_ForSharedExecutables()
    {
        var serviceType = typeof(StoreSyncService);
        var storeGameEntryType = serviceType.GetNestedType("StoreGameEntry", BindingFlags.NonPublic);
        var existingShortcutEntryType = serviceType.GetNestedType("ExistingShortcutEntry", BindingFlags.NonPublic);
        var method = serviceType.GetMethod("TryFindExistingShortcut", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(storeGameEntryType);
        Assert.NotNull(existingShortcutEntryType);
        Assert.NotNull(method);

        var game = CreateNonPublicInstance(
            storeGameEntryType!,
            "ubisoft-connect",
            "ubisoft|anno",
            "Anno 117 Pax Romana",
            @"C:\Games\Ubisoft\UbisoftConnect.exe",
            @"C:\Games\Ubisoft",
            "uplay://launch/1234/0");

        var entries = Array.CreateInstance(existingShortcutEntryType!, 2);
        entries.SetValue(
            CreateNonPublicInstance(
                existingShortcutEntryType!,
                0,
                1001u,
                "Anno 117 Pax Romana",
                @"C:\Games\Ubisoft\UbisoftConnect.exe",
                @"C:\Games\Ubisoft",
                "uplay://launch/9999/0",
                false,
                string.Empty,
                string.Empty,
                new Dictionary<string, object?>()),
            0);
        entries.SetValue(
            CreateNonPublicInstance(
                existingShortcutEntryType!,
                1,
                1002u,
                "Anno 117 Pax Romana",
                @"C:\Games\Ubisoft\UbisoftConnect.exe",
                @"C:\Games\Ubisoft",
                "uplay://launch/1234/0",
                false,
                string.Empty,
                string.Empty,
                new Dictionary<string, object?>()),
            1);

        var arguments = new object?[] { entries, game, null, "Anno 117 Pax Romana", null };
        var matched = (bool?)method!.Invoke(null, arguments);

        Assert.True(matched);
        Assert.NotNull(arguments[4]);

        var matchedShortcut = arguments[4]!;
        var appId = (uint?)existingShortcutEntryType!.GetProperty("AppId")?.GetValue(matchedShortcut);
        Assert.Equal(1002u, appId);
    }

    [Fact]
    public void OwnershipRepairMatches_RejectsDifferentLaunchOptions_ForSharedExecutables()
    {
        var serviceType = typeof(StoreSyncService);
        var storeDefinitionType = serviceType.GetNestedType("StoreDefinition", BindingFlags.NonPublic);
        var storeGameEntryType = serviceType.GetNestedType("StoreGameEntry", BindingFlags.NonPublic);
        var actionKindType = serviceType.GetNestedType("StoreSyncActionKind", BindingFlags.NonPublic);
        var analysisItemType = serviceType.GetNestedType("StoreSyncAnalysisItem", BindingFlags.NonPublic);
        var existingShortcutEntryType = serviceType.GetNestedType("ExistingShortcutEntry", BindingFlags.NonPublic);
        var method = serviceType.GetMethod("OwnershipRepairMatches", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(storeDefinitionType);
        Assert.NotNull(storeGameEntryType);
        Assert.NotNull(actionKindType);
        Assert.NotNull(analysisItemType);
        Assert.NotNull(existingShortcutEntryType);
        Assert.NotNull(method);

        var definition = CreateNonPublicInstance(
            storeDefinitionType!,
            "ubisoft-connect",
            "Ubisoft Connect",
            "Test store",
            true,
            false);
        var game = CreateNonPublicInstance(
            storeGameEntryType!,
            "ubisoft-connect",
            "ubisoft|anno",
            "Anno 117 Pax Romana",
            @"C:\Games\Ubisoft\UbisoftConnect.exe",
            @"C:\Games\Ubisoft",
            "uplay://launch/1234/0");
        var actionKind = Enum.Parse(actionKindType!, "RefreshManaged");
        var analysisItem = CreateNonPublicInstance(
            analysisItemType!,
            "ubisoft-connect-anno",
            "ubisoft-connect-anno",
            definition,
            game,
            new StoreSyncTitleOverride(),
            "Anno 117 Pax Romana",
            "Anno 117 Pax Romana",
            1002u,
            actionKind,
            "Refresh Managed",
            string.Empty,
            null,
            null,
            null,
            Array.Empty<string>());

        var entry = CreateNonPublicInstance(
            existingShortcutEntryType!,
            1,
            1001u,
            "Anno 117 Pax Romana",
            @"C:\Games\Ubisoft\UbisoftConnect.exe",
            @"C:\Games\Ubisoft",
            "uplay://launch/9999/0",
            true,
            "ubisoft-connect",
            "ubisoft-connect-anno",
            new Dictionary<string, object?>());

        var matches = (bool?)method!.Invoke(null, [entry, analysisItem, 0u]);

        Assert.False(matches);
    }

    [Fact]
    public void TryDeserializeMatchingResponse_IgnoresUnrelatedDevToolsMessages()
    {
        var method = typeof(SteamDevToolsClient).GetMethod(
            "TryDeserializeMatchingResponse",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var ignored = method!.Invoke(null, ["{\"method\":\"Runtime.consoleAPICalled\",\"params\":{}}", 7]);
        var mismatched = method.Invoke(null, ["{\"id\":6,\"result\":{\"result\":{\"type\":\"string\",\"value\":\"nope\"}}}", 7]);
        var matched = method.Invoke(null, ["{\"id\":7,\"result\":{\"result\":{\"type\":\"string\",\"value\":\"ok\"}}}", 7]);

        Assert.Null(ignored);
        Assert.Null(mismatched);
        Assert.NotNull(matched);
    }

    [Fact]
    public void CurrentScriptVersionValue_AcceptsDevToolsJsonBooleanWithoutReinjecting()
    {
        using var trueDocument = JsonDocument.Parse("true");
        using var falseDocument = JsonDocument.Parse("false");

        Assert.True(QuickAccessShellInjector.IsCurrentScriptVersionValue(trueDocument.RootElement));
        Assert.True(SteamDevToolsClient.TryReadBoolean(trueDocument.RootElement, out var parsed));
        Assert.True(parsed);
        Assert.False(QuickAccessShellInjector.IsCurrentScriptVersionValue(falseDocument.RootElement));
    }

    [Fact]
    public void BuildLiveShortcutSyncExpression_AlwaysCreatesNewShortcutsThroughAddShortcut()
    {
        var serviceType = typeof(StoreSyncService);
        var createOperationType = serviceType.GetNestedType("LiveShortcutSyncCreateOperation", BindingFlags.NonPublic);
        var updateOperationType = serviceType.GetNestedType("LiveShortcutSyncUpdateOperation", BindingFlags.NonPublic);
        var removeOperationType = serviceType.GetNestedType("LiveShortcutSyncRemoveOperation", BindingFlags.NonPublic);
        var planType = serviceType.GetNestedType("LiveShortcutSyncPlan", BindingFlags.NonPublic);
        var method = serviceType.GetMethod("BuildLiveShortcutSyncExpression", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(createOperationType);
        Assert.NotNull(updateOperationType);
        Assert.NotNull(removeOperationType);
        Assert.NotNull(planType);
        Assert.NotNull(method);

        var createOperation = CreateNonPublicInstance(
            createOperationType!,
            "xbox-game-pass-quake",
            3155387848u,
            "QUAKE",
            @"D:\XboxGames\QUAKE\Content\bastet_WinStore.exe",
            @"D:\XboxGames\QUAKE\Content",
            string.Empty,
            @"D:\XboxGames\QUAKE\Content\bastet_WinStore.exe");
        var createOperations = Array.CreateInstance(createOperationType!, 1);
        createOperations.SetValue(createOperation, 0);
        var updateOperations = Array.CreateInstance(updateOperationType!, 0);
        var removeOperations = Array.CreateInstance(removeOperationType!, 0);
        var plan = CreateNonPublicInstance(
            planType!,
            "226501611",
            true,
            createOperations,
            updateOperations,
            removeOperations);

        var expression = Assert.IsType<string>(method!.Invoke(null, [plan]));
        var createMarker = "for (const operation of plan.createOperations ?? [])";
        var updateMarker = "for (const operation of plan.updateOperations ?? [])";
        var createStart = expression.IndexOf(createMarker, StringComparison.Ordinal);
        var updateStart = expression.IndexOf(updateMarker, StringComparison.Ordinal);

        Assert.True(createStart >= 0, "The live sync JavaScript should include a create loop.");
        Assert.True(updateStart > createStart, "The update loop should appear after the create loop.");

        var createBlock = expression.Substring(createStart, updateStart - createStart);
        Assert.Contains("\"AddShortcut\"", createBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("tryApplyToShortcut", createBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void GetManifestMatchScore_OmniLibraryRejectsSharedExecutableForDifferentStoreProduct()
    {
        var serviceType = typeof(StoreSyncService);
        var storeGameEntryType = serviceType.GetNestedType("StoreGameEntry", BindingFlags.NonPublic);
        var method = serviceType.GetMethod("GetManifestMatchScore", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(storeGameEntryType);
        Assert.NotNull(method);

        var game = CreateNonPublicInstance(
            storeGameEntryType!,
            "unifysteam",
            "xbox-game-pass:PRODUCT-B",
            "Cloud Game B",
            @"C:\ToolsForSteam\ToolsForSteam.exe",
            @"C:\ToolsForSteam",
            "--unifysteam-launch xbox-game-pass:PRODUCT-B");
        var unrelatedManifest = new StoreSyncManifestEntry
        {
            TitleId = "unifysteam-product-a",
            StoreId = "unifysteam",
            StoreItemId = "xbox-game-pass:PRODUCT-A",
            Title = "Cloud Game A",
            EffectiveTitle = "Cloud Game A",
            ExecutablePath = @"C:\ToolsForSteam\ToolsForSteam.exe",
            AppId = 2214928963u,
            ManagedShortcut = true,
        };

        var score = (int?)method!.Invoke(
            null,
            [unrelatedManifest, game, "Cloud Game B", null]);

        Assert.Equal(int.MaxValue, score);
    }

    [Fact]
    public void GetOmniLibraryDuplicateSteamAppIdGameIds_ReturnsEveryCollidingProduct()
    {
        var method = typeof(StoreSyncService).GetMethod(
            "GetOmniLibraryDuplicateSteamAppIdGameIds",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var store = new UnifySteamStoreConfiguration
        {
            Cache = new UnifySteamLibraryCache
            {
                Games =
                [
                    new() { Id = "PRODUCT-A", SteamAppId = 2214928963u },
                    new() { Id = "PRODUCT-B", SteamAppId = 2214928963u },
                    new() { Id = "PRODUCT-C", SteamAppId = 991u },
                ],
            },
        };

        var result = Assert.IsType<string[]>(method!.Invoke(null, [store]));

        Assert.Equal(["PRODUCT-A", "PRODUCT-B"], result);
    }

    [Fact]
    public void BuildLiveShortcutSyncPlan_ForcesDistinctOmniShortcutsForDuplicateAppId()
    {
        var serviceType = typeof(StoreSyncService);
        var storeDefinitionType = serviceType.GetNestedType("StoreDefinition", BindingFlags.NonPublic);
        var storeGameEntryType = serviceType.GetNestedType("StoreGameEntry", BindingFlags.NonPublic);
        var actionKindType = serviceType.GetNestedType("StoreSyncActionKind", BindingFlags.NonPublic);
        var analysisItemType = serviceType.GetNestedType("StoreSyncAnalysisItem", BindingFlags.NonPublic);
        var analysisType = serviceType.GetNestedType("StoreSyncAnalysis", BindingFlags.NonPublic);
        var existingShortcutEntryType = serviceType.GetNestedType("ExistingShortcutEntry", BindingFlags.NonPublic);
        var method = serviceType.GetMethod("BuildLiveShortcutSyncPlan", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(storeDefinitionType);
        Assert.NotNull(storeGameEntryType);
        Assert.NotNull(actionKindType);
        Assert.NotNull(analysisItemType);
        Assert.NotNull(analysisType);
        Assert.NotNull(existingShortcutEntryType);
        Assert.NotNull(method);

        const uint collidedAppId = 2214928963u;
        var definition = CreateNonPublicInstance(
            storeDefinitionType!,
            "unifysteam",
            "Storefront",
            "Test store",
            true,
            false);
        var actionKind = Enum.Parse(actionKindType!, "RefreshManaged");
        var analysisItems = Array.CreateInstance(analysisItemType!, 2);

        for (var index = 0; index < 2; index++)
        {
            var suffix = index == 0 ? "A" : "B";
            var titleId = $"unifysteam-product-{suffix.ToLowerInvariant()}";
            var launchOptions = $"--unifysteam-launch xbox-game-pass:PRODUCT-{suffix}";
            var game = CreateNonPublicInstance(
                storeGameEntryType!,
                "unifysteam",
                $"xbox-game-pass:PRODUCT-{suffix}",
                $"Cloud Game {suffix}",
                @"C:\ToolsForSteam\ToolsForSteam.exe",
                @"C:\ToolsForSteam",
                launchOptions);
            var entry = new Dictionary<string, object?>
            {
                ["appid"] = unchecked((int)collidedAppId),
                ["appname"] = $"Cloud Game {suffix}",
                ["Exe"] = "\"C:\\ToolsForSteam\\ToolsForSteam.exe\"",
                ["StartDir"] = "\"C:\\ToolsForSteam\"",
                ["LaunchOptions"] = launchOptions,
                ["tags"] = new Dictionary<string, object?>
                {
                    ["2"] = "unifysteam",
                    ["3"] = titleId,
                },
            };
            var existingShortcut = CreateNonPublicInstance(
                existingShortcutEntryType!,
                index,
                collidedAppId,
                $"Cloud Game {suffix}",
                @"C:\ToolsForSteam\ToolsForSteam.exe",
                @"C:\ToolsForSteam",
                launchOptions,
                true,
                "unifysteam",
                titleId,
                entry);
            var manifest = new StoreSyncManifestEntry
            {
                TitleId = titleId,
                StoreId = "unifysteam",
                StoreItemId = $"xbox-game-pass:PRODUCT-{suffix}",
                Title = $"Cloud Game {suffix}",
                EffectiveTitle = $"Cloud Game {suffix}",
                ExecutablePath = @"C:\ToolsForSteam\ToolsForSteam.exe",
                AppId = collidedAppId,
                ManagedShortcut = true,
            };
            var item = CreateNonPublicInstance(
                analysisItemType!,
                titleId,
                titleId,
                definition,
                game,
                new StoreSyncTitleOverride(),
                $"Cloud Game {suffix}",
                $"Cloud Game {suffix}",
                collidedAppId,
                actionKind,
                "Refresh Managed",
                string.Empty,
                existingShortcut,
                manifest,
                null,
                Array.Empty<string>());
            analysisItems.SetValue(item, index);
        }

        var emptyCleanupCandidates = Array.CreateInstance(
            serviceType.GetNestedType("StoreSyncCleanupCandidate", BindingFlags.NonPublic)!,
            0);
        var analysis = CreateNonPublicInstance(
            analysisType!,
            analysisItems,
            emptyCleanupCandidates,
            0,
            new StoreSyncPreviewState(0, 2, 0, 0, 0, 0, 0, []));

        var plan = method!.Invoke(
            null,
            [
                new StoreSyncConfiguration(),
                new SteamProfileInfo("User", "user", "1", "1", @"C:\Steam\shortcuts.vdf"),
                analysis
            ]);

        Assert.NotNull(plan);
        var updates = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
                plan!.GetType().GetProperty("UpdateOperations")!.GetValue(plan))
            .Cast<object>()
            .ToArray();
        Assert.Equal(2, updates.Length);
        Assert.Single(updates, update => (bool)update.GetType().GetProperty("ForceCreate")!.GetValue(update)!);
        Assert.Single(updates, update => !(bool)update.GetType().GetProperty("ForceCreate")!.GetValue(update)!);
    }

    [Fact]
    public void HasMeaningfulSyncWork_ReturnsFalse_ForSkipOnlyPlan()
    {
        var serviceType = typeof(StoreSyncService);
        var storeDefinitionType = serviceType.GetNestedType("StoreDefinition", BindingFlags.NonPublic);
        var storeGameEntryType = serviceType.GetNestedType("StoreGameEntry", BindingFlags.NonPublic);
        var actionKindType = serviceType.GetNestedType("StoreSyncActionKind", BindingFlags.NonPublic);
        var analysisItemType = serviceType.GetNestedType("StoreSyncAnalysisItem", BindingFlags.NonPublic);
        var analysisType = serviceType.GetNestedType("StoreSyncAnalysis", BindingFlags.NonPublic);
        var method = serviceType.GetMethod("HasMeaningfulSyncWork", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(storeDefinitionType);
        Assert.NotNull(storeGameEntryType);
        Assert.NotNull(actionKindType);
        Assert.NotNull(analysisItemType);
        Assert.NotNull(analysisType);
        Assert.NotNull(method);

        var definition = CreateNonPublicInstance(
            storeDefinitionType!,
            "ea-app",
            "EA App",
            "Test store",
            true,
            false);
        var game = CreateNonPublicInstance(
            storeGameEntryType!,
            "ea-app",
            "ea|dead-space",
            "Dead Space",
            @"C:\Games\EA\DeadSpace.exe",
            @"C:\Games\EA",
            string.Empty);
        var actionKind = Enum.Parse(actionKindType!, "SkipExisting");
        var item = CreateNonPublicInstance(
            analysisItemType!,
            "ea-app-dead-space",
            "ea-app-dead-space",
            definition,
            game,
            new StoreSyncTitleOverride(),
            "Dead Space",
            "Dead Space",
            0u,
            actionKind,
            "Skip Existing",
            string.Empty,
            null,
            null,
            null,
            Array.Empty<string>());
        var analysisItems = Array.CreateInstance(analysisItemType!, 1);
        analysisItems.SetValue(item, 0);
        var emptyCleanupCandidates = Array.CreateInstance(
            serviceType.GetNestedType("StoreSyncCleanupCandidate", BindingFlags.NonPublic)!,
            0);
        var preview = new StoreSyncPreviewState(0, 0, 0, 1, 0, 0, 0, []);
        var analysis = CreateNonPublicInstance(
            analysisType!,
            analysisItems,
            emptyCleanupCandidates,
            0,
            preview);

        var service = RuntimeHelpers.GetUninitializedObject(serviceType);
        var result = (bool?)method!.Invoke(service, [analysis]);

        Assert.False(result);
    }

    [Fact]
    public void HasMeaningfulSyncWork_ReturnsTrue_ForCleanupPlan()
    {
        var serviceType = typeof(StoreSyncService);
        var existingShortcutEntryType = serviceType.GetNestedType("ExistingShortcutEntry", BindingFlags.NonPublic);
        var cleanupCandidateType = serviceType.GetNestedType("StoreSyncCleanupCandidate", BindingFlags.NonPublic);
        var analysisType = serviceType.GetNestedType("StoreSyncAnalysis", BindingFlags.NonPublic);
        var method = serviceType.GetMethod("HasMeaningfulSyncWork", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(existingShortcutEntryType);
        Assert.NotNull(cleanupCandidateType);
        Assert.NotNull(analysisType);
        Assert.NotNull(method);

        var shortcutEntry = CreateNonPublicInstance(
            existingShortcutEntryType!,
            0,
            4040u,
            "Dead Space",
            @"C:\Games\EA\DeadSpace.exe",
            @"C:\Games\EA",
            string.Empty,
            true,
            "ea-app",
            "ea-app-dead-space",
            new Dictionary<string, object?>());
        var cleanupCandidate = CreateNonPublicInstance(
            cleanupCandidateType!,
            "ea-app-dead-space",
            "Dead Space",
            "EA App",
            shortcutEntry,
            null,
            Array.Empty<string>());
        var emptyAnalysisItems = Array.CreateInstance(
            serviceType.GetNestedType("StoreSyncAnalysisItem", BindingFlags.NonPublic)!,
            0);
        var cleanupCandidates = Array.CreateInstance(cleanupCandidateType!, 1);
        cleanupCandidates.SetValue(cleanupCandidate, 0);
        var preview = new StoreSyncPreviewState(0, 0, 0, 0, 0, 1, 0, []);
        var analysis = CreateNonPublicInstance(
            analysisType!,
            emptyAnalysisItems,
            cleanupCandidates,
            0,
            preview);

        var service = RuntimeHelpers.GetUninitializedObject(serviceType);
        var result = (bool?)method!.Invoke(service, [analysis]);

        Assert.True(result);
    }

    private static object CreateNonPublicInstance(Type type, params object?[] arguments)
    {
        var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(info => info.GetParameters().Length == arguments.Length);

        return constructor.Invoke(arguments);
    }
}
