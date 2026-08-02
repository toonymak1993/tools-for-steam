using System.Reflection;
using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class StoreSyncArtworkProtectionTests
{
    [Fact]
    public void CompleteArtworkSet_RequiresEverySteamLibrarySlot()
    {
        var root = CreateTempRoot();

        try
        {
            const uint appId = 2362688489;
            var gridDirectory = Path.Combine(root, "grid");
            Directory.CreateDirectory(gridDirectory);
            var gridId = SteamShortcutIds.BuildGridId(appId);

            File.WriteAllBytes(Path.Combine(gridDirectory, $"{gridId}.png"), ValidPngBytes());
            File.WriteAllBytes(Path.Combine(gridDirectory, $"{gridId}p.png"), ValidPngBytes());
            File.WriteAllBytes(Path.Combine(gridDirectory, $"{gridId}_hero.png"), ValidPngBytes());

            Assert.True(SteamGridDbArtworkDownloader.HasPrimaryArtworkSet(gridDirectory, appId));
            Assert.False(SteamGridDbArtworkDownloader.HasCompleteArtworkSet(gridDirectory, appId));
            Assert.Equal(
                ["logo", "icon"],
                SteamGridDbArtworkDownloader.GetMissingArtworkSlots(gridDirectory, appId));

            File.WriteAllBytes(Path.Combine(gridDirectory, $"{gridId}_logo.png"), ValidPngBytes());
            File.WriteAllBytes(Path.Combine(gridDirectory, $"{gridId}-icon.png"), ValidPngBytes());

            Assert.True(SteamGridDbArtworkDownloader.HasCompleteArtworkSet(gridDirectory, appId));
            Assert.Empty(SteamGridDbArtworkDownloader.GetMissingArtworkSlots(gridDirectory, appId));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void CompleteArtworkSet_RejectsTruncatedOrInvalidCacheFiles()
    {
        var root = CreateTempRoot();

        try
        {
            const uint appId = 2377733344;
            var gridDirectory = Path.Combine(root, "grid");
            Directory.CreateDirectory(gridDirectory);
            var gridId = SteamShortcutIds.BuildGridId(appId);
            foreach (var stem in new[]
                     {
                         gridId,
                         $"{gridId}p",
                         $"{gridId}_hero",
                         $"{gridId}_logo",
                         $"{gridId}-icon",
                     })
            {
                File.WriteAllBytes(Path.Combine(gridDirectory, $"{stem}.png"), ValidPngBytes());
            }

            File.WriteAllBytes(Path.Combine(gridDirectory, $"{gridId}_hero.png"), new byte[512]);

            Assert.False(SteamGridDbArtworkDownloader.HasCompleteArtworkSet(gridDirectory, appId));
            Assert.Contains(
                "hero",
                SteamGridDbArtworkDownloader.GetMissingArtworkSlots(gridDirectory, appId));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void ShouldUpdateArtworkForItem_DelaysRetryAfterIncompleteArtworkAttempt()
    {
        var root = CreateTempRoot();

        try
        {
            const uint appId = 3132913252;
            var gridDirectory = Path.Combine(root, "grid");
            Directory.CreateDirectory(gridDirectory);
            File.WriteAllBytes(Path.Combine(gridDirectory, $"{appId}p.png"), [1]);

            var serviceType = typeof(StoreSyncService);
            var storeDefinitionType = serviceType.GetNestedType("StoreDefinition", BindingFlags.NonPublic)!;
            var storeGameEntryType = serviceType.GetNestedType("StoreGameEntry", BindingFlags.NonPublic)!;
            var actionKindType = serviceType.GetNestedType("StoreSyncActionKind", BindingFlags.NonPublic)!;
            var analysisItemType = serviceType.GetNestedType("StoreSyncAnalysisItem", BindingFlags.NonPublic)!;
            var method = serviceType.GetMethod("ShouldUpdateArtworkForItem", BindingFlags.NonPublic | BindingFlags.Static)!;

            var definition = CreateNonPublicInstance(
                storeDefinitionType,
                "xbox-game-pass",
                "Xbox / Game Pass",
                "Test store",
                false,
                true);
            var game = CreateNonPublicInstance(
                storeGameEntryType,
                "xbox-game-pass",
                "xbox|high-on-life-2",
                "High On Life 2",
                @"C:\XboxGames\High On Life 2\Content\HighOnLife2.exe",
                @"C:\XboxGames\High On Life 2\Content",
                string.Empty);
            var manifestEntry = new StoreSyncManifestEntry
            {
                TitleId = "xbox-game-pass-high-on-life-2",
                StoreId = "xbox-game-pass",
                AppId = appId,
                ManagedShortcut = true,
                ArtworkLocked = true,
                LastArtworkAttemptAtUtc = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(5)),
            };
            var artworkCache = new StoreSyncArtworkCacheEntry
            {
                GameId = 5491700,
                MatchName = "High On Life 2",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            var analysisItem = CreateNonPublicInstance(
                analysisItemType,
                manifestEntry.TitleId,
                manifestEntry.TitleId,
                definition,
                game,
                new StoreSyncTitleOverride(),
                "High On Life 2",
                "High On Life 2",
                appId,
                Enum.Parse(actionKindType, "RefreshManaged"),
                "Refresh Managed",
                string.Empty,
                null,
                manifestEntry,
                artworkCache,
                Array.Empty<string>());

            var shouldUpdate = (bool?)method.Invoke(null, [analysisItem, appId, artworkCache, gridDirectory]);

            Assert.False(shouldUpdate);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

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

    private static byte[] ValidPngBytes()
    {
        var bytes = new byte[256];
        byte[] signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        signature.CopyTo(bytes, 0);
        return bytes;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
