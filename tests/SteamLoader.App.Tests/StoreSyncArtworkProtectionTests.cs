using System.Reflection;
using System.Drawing;
using System.Drawing.Imaging;
using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class StoreSyncArtworkProtectionTests
{
    [Fact]
    public void LocalArtworkResolver_UsesSteamLibraryCacheBeforeRemoteSources()
    {
        var root = CreateTempRoot();

        try
        {
            var steamApps = Path.Combine(root, "steamapps");
            var cache = Path.Combine(root, "appcache", "librarycache", "424242");
            Directory.CreateDirectory(steamApps);
            Directory.CreateDirectory(cache);
            File.WriteAllText(
                Path.Combine(steamApps, "appmanifest_424242.acf"),
                "\"AppState\"\n{\n\t\"appid\"\t\"424242\"\n\t\"name\"\t\"Local Cache Test Game\"\n}");
            WriteTestImage(Path.Combine(cache, "header.jpg"), 920, 430, ImageFormat.Jpeg);
            WriteTestImage(Path.Combine(cache, "library_600x900.jpg"), 600, 900, ImageFormat.Jpeg);
            WriteTestImage(Path.Combine(cache, "library_hero.jpg"), 1920, 620, ImageFormat.Jpeg);
            WriteTestImage(Path.Combine(cache, "logo.png"), 800, 240, ImageFormat.Png);

            var resolver = new OmniLibraryLocalArtworkResolver(root);
            var target = new StoreSyncArtworkTarget(
                "epic-games:local-cache-test",
                "Local Cache Test Game",
                3000000001,
                ["Local Cache Test Game"],
                null,
                string.Empty,
                "epic-games");

            var resolved = resolver.Resolve(target);

            Assert.Equal(
                Path.Combine(cache, "header.jpg"),
                resolved[OmniLibraryArtworkSlotKind.LibraryCapsule]);
            Assert.Equal(
                Path.Combine(cache, "library_600x900.jpg"),
                resolved[OmniLibraryArtworkSlotKind.Portrait]);
            Assert.Equal(
                Path.Combine(cache, "library_hero.jpg"),
                resolved[OmniLibraryArtworkSlotKind.Hero]);
            Assert.Equal(
                Path.Combine(cache, "logo.png"),
                resolved[OmniLibraryArtworkSlotKind.Logo]);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void LocalArtworkResolver_UsesBoundedInstallArtworkAndExplicitFiles()
    {
        var root = CreateTempRoot();

        try
        {
            var installPath = Path.Combine(root, "game");
            var artworkPath = Path.Combine(installPath, "artwork");
            Directory.CreateDirectory(artworkPath);
            var explicitPortrait = Path.Combine(root, "exact-cover.png");
            var discoveredPortrait = Path.Combine(artworkPath, "boxart.png");
            var discoveredHero = Path.Combine(artworkPath, "library-hero.jpg");
            var discoveredLogo = Path.Combine(artworkPath, "clearlogo.png");
            WriteTestImage(explicitPortrait, 600, 900, ImageFormat.Png);
            WriteTestImage(discoveredPortrait, 600, 900, ImageFormat.Png);
            WriteTestImage(discoveredHero, 1920, 620, ImageFormat.Jpeg);
            WriteTestImage(discoveredLogo, 800, 240, ImageFormat.Png);

            var resolver = new OmniLibraryLocalArtworkResolver(
                Path.Combine(root, "missing-steam"));
            var target = new StoreSyncArtworkTarget(
                "gog:test",
                "Install Artwork Test",
                3000000002,
                ["Install Artwork Test"],
                null,
                string.Empty,
                "gog-galaxy",
                new Uri(explicitPortrait).AbsoluteUri,
                string.Empty,
                LocalInstallPath: installPath);

            var resolved = resolver.Resolve(target);

            Assert.Equal(
                explicitPortrait,
                resolved[OmniLibraryArtworkSlotKind.Portrait]);
            Assert.Equal(
                discoveredHero,
                resolved[OmniLibraryArtworkSlotKind.Hero]);
            Assert.Equal(
                discoveredLogo,
                resolved[OmniLibraryArtworkSlotKind.Logo]);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task DownloadLocalFirst_CompletesEverySlotWithoutSteamGridDb()
    {
        var root = CreateTempRoot();

        try
        {
            const uint appId = 3000000003;
            var gridDirectory = Path.Combine(root, "grid");
            var portraitPath = Path.Combine(root, "portrait.png");
            var heroPath = Path.Combine(root, "hero.jpg");
            WriteTestImage(portraitPath, 600, 900, ImageFormat.Png);
            WriteTestImage(heroPath, 1920, 620, ImageFormat.Jpeg);
            var target = new StoreSyncArtworkTarget(
                "xbox-game-pass:local-only",
                "A Local Only Artwork Test That Does Not Exist",
                appId,
                ["A Local Only Artwork Test That Does Not Exist"],
                null,
                string.Empty,
                string.Empty,
                new Uri(portraitPath).AbsoluteUri,
                new Uri(heroPath).AbsoluteUri);

            var summary = await new SteamGridDbArtworkDownloader().DownloadLocalFirstAsync(
                gridDirectory,
                [target],
                string.Empty,
                CancellationToken.None);

            Assert.Equal(1, summary.UpdatedTitleCount);
            Assert.True(SteamGridDbArtworkDownloader.HasCompleteArtworkSet(gridDirectory, appId));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ForceReload_StagesCompleteSetBeforeReplacingManagedArtwork()
    {
        var root = CreateTempRoot();

        try
        {
            const uint appId = 3000000004;
            const uint unrelatedAppId = 3000000005;
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
                WriteTestImage(
                    Path.Combine(gridDirectory, $"{stem}.png"),
                    320,
                    180,
                    ImageFormat.Png,
                    Color.DarkRed);
            }

            var unrelatedPath = Path.Combine(
                gridDirectory,
                $"{SteamShortcutIds.BuildGridId(unrelatedAppId)}.png");
            WriteTestImage(
                unrelatedPath,
                320,
                180,
                ImageFormat.Png,
                Color.DarkOrange);
            var unrelatedBefore = File.ReadAllBytes(unrelatedPath);
            var portraitPath = Path.Combine(root, "replacement-portrait.png");
            var heroPath = Path.Combine(root, "replacement-hero.jpg");
            WriteTestImage(
                portraitPath,
                600,
                900,
                ImageFormat.Png,
                Color.DarkGreen);
            WriteTestImage(
                heroPath,
                1920,
                620,
                ImageFormat.Jpeg,
                Color.DarkGreen);
            var portraitBefore = File.ReadAllBytes(
                Path.Combine(gridDirectory, $"{gridId}p.png"));
            var target = new StoreSyncArtworkTarget(
                "xbox-game-pass:reload-test",
                "Artwork Reload Test",
                appId,
                ["Artwork Reload Test"],
                null,
                string.Empty,
                "xbox-game-pass",
                new Uri(portraitPath).AbsoluteUri,
                new Uri(heroPath).AbsoluteUri,
                ForceReload: true);

            var summary = await new SteamGridDbArtworkDownloader().DownloadLocalFirstAsync(
                gridDirectory,
                [target],
                string.Empty,
                CancellationToken.None);

            Assert.Equal(1, summary.UpdatedTitleCount);
            Assert.True(SteamGridDbArtworkDownloader.HasCompleteArtworkSet(gridDirectory, appId));
            Assert.NotEqual(
                portraitBefore,
                File.ReadAllBytes(Path.Combine(gridDirectory, $"{gridId}p.png")));
            Assert.Equal(unrelatedBefore, File.ReadAllBytes(unrelatedPath));
            Assert.Empty(Directory.EnumerateDirectories(
                gridDirectory,
                ".tfs-artwork-reload-*",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

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
    public async Task DownloadAssetToTemporaryFile_FlushesBeforeValidatingLocalArtwork()
    {
        var root = CreateTempRoot();
        string? downloadedPath = null;

        try
        {
            var sourcePath = Path.Combine(root, "source.png");
            File.WriteAllBytes(sourcePath, ValidPngBytes());
            using var httpClient = new HttpClient();
            var method = typeof(SteamGridDbArtworkDownloader).GetMethod(
                "DownloadAssetToTemporaryFileAsync",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var task = (Task)method!.Invoke(
                null,
                [httpClient, new Uri(sourcePath).AbsoluteUri, CancellationToken.None])!;
            await task;

            var result = task.GetType().GetProperty("Result")!.GetValue(task);
            Assert.NotNull(result);
            downloadedPath = (string)result!.GetType().GetProperty("TempPath")!.GetValue(result)!;
            Assert.True(File.Exists(downloadedPath));
            Assert.Equal(ValidPngBytes().Length, new FileInfo(downloadedPath).Length);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(downloadedPath) && File.Exists(downloadedPath))
            {
                File.Delete(downloadedPath);
            }

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

    private static void WriteTestImage(
        string path,
        int width,
        int height,
        ImageFormat format,
        Color? color = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(color ?? Color.DarkSlateBlue);
        bitmap.Save(path, format);
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
