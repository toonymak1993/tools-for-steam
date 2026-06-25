using System.Reflection;
using System.Runtime.CompilerServices;
using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Models;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class StoreSyncLiveSyncHardeningTests
{
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
