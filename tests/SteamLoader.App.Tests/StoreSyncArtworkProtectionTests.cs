using System.Reflection;
using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class StoreSyncArtworkProtectionTests
{
    [Fact]
    public void ShouldUpdateArtworkForItem_PreservesExistingManagedArtwork()
    {
        var root = CreateTempRoot();

        try
        {
            const uint appId = 2301669340;
            var gridDirectory = Path.Combine(root, "grid");
            Directory.CreateDirectory(gridDirectory);
            File.WriteAllBytes(Path.Combine(gridDirectory, $"{appId}.png"), [1]);
            File.WriteAllBytes(Path.Combine(gridDirectory, $"{appId}p.png"), [1]);

            var serviceType = typeof(StoreSyncService);
            var storeDefinitionType = serviceType.GetNestedType("StoreDefinition", BindingFlags.NonPublic);
            var storeGameEntryType = serviceType.GetNestedType("StoreGameEntry", BindingFlags.NonPublic);
            var actionKindType = serviceType.GetNestedType("StoreSyncActionKind", BindingFlags.NonPublic);
            var analysisItemType = serviceType.GetNestedType("StoreSyncAnalysisItem", BindingFlags.NonPublic);
            var method = serviceType.GetMethod("ShouldUpdateArtworkForItem", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(storeDefinitionType);
            Assert.NotNull(storeGameEntryType);
            Assert.NotNull(actionKindType);
            Assert.NotNull(analysisItemType);
            Assert.NotNull(method);

            var definition = CreateNonPublicInstance(
                storeDefinitionType!,
                "ubisoft-connect",
                "Ubisoft Connect",
                "Test store",
                false,
                false);
            var game = CreateNonPublicInstance(
                storeGameEntryType!,
                "ubisoft-connect",
                "ubisoft|anno",
                "Anno 117 Pax Romana",
                @"C:\Games\Anno117.exe",
                @"C:\Games",
                string.Empty);
            var actionKind = Enum.Parse(actionKindType!, "RefreshManaged");

            var manifestEntry = new StoreSyncManifestEntry
            {
                TitleId = "ubisoft-connect-anno",
                StoreId = "ubisoft-connect",
                StoreItemId = "ubisoft|anno",
                Title = "Anno 117 Pax Romana",
                EffectiveTitle = "Anno 117 Pax Romana",
                ExecutablePath = @"C:\Games\Anno117.exe",
                AppId = appId,
                ManagedShortcut = true,
                AdoptedExistingShortcut = false,
                LastAction = "Refresh Managed",
                LastDetail = "An existing Tools for Steam shortcut will be refreshed.",
                SteamGridDbGameId = 11,
                ArtworkTitle = "Old Artwork Title",
                ArtworkLocked = false,
                LastSeenAtUtc = DateTimeOffset.UtcNow,
                LastUpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            var artworkCache = new StoreSyncArtworkCacheEntry
            {
                GameId = 22,
                MatchName = "New Artwork Title",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            var analysisItem = CreateNonPublicInstance(
                analysisItemType!,
                "ubisoft-connect-anno",
                "ubisoft-connect-anno",
                definition,
                game,
                new StoreSyncTitleOverride(),
                "Anno 117 Pax Romana",
                "New Artwork Title",
                appId,
                actionKind,
                "Refresh Managed",
                "SteamGridDB will search for New Artwork Title.",
                null,
                manifestEntry,
                artworkCache,
                Array.Empty<string>());

            var shouldUpdate = (bool?)method!.Invoke(null, [analysisItem, appId, artworkCache, gridDirectory]);

            Assert.False(shouldUpdate);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static object CreateNonPublicInstance(Type type, params object?[] arguments)
    {
        var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(info => info.GetParameters().Length == arguments.Length);

        return constructor.Invoke(arguments);
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
}
