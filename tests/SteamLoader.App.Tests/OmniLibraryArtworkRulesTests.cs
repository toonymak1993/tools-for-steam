using System.Drawing;
using System.Drawing.Imaging;
using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

/// <summary>Parity checks for the rules ported from ORBIT's artwork pipeline.</summary>
public sealed class OmniLibraryArtworkRulesTests
{
    [Theory]
    [InlineData("Final Fantasy VII™", "final fantasy 7")]
    [InlineData("Pokémon: Let's Go, Pikachu!", "Pokemon Let s Go Pikachu")]
    [InlineData("DOOM II", "Doom 2")]
    public void CanonicalTitle_IgnoresPunctuationMarksAndRomanNumerals(string left, string right)
    {
        Assert.Equal(
            OmniLibraryArtworkRules.CanonicalArtworkTitle(left),
            OmniLibraryArtworkRules.CanonicalArtworkTitle(right));
    }

    [Fact]
    public void SteamGridDbSelection_AcceptsOnlyExactOrEditionStrippedTitles()
    {
        string[] hades = ["Hades II", "Hades"];
        Assert.Equal("Hades", OmniLibraryArtworkRules.SelectSteamGridDbGame("Hades", hades, name => name));
        Assert.Null(OmniLibraryArtworkRules.SelectSteamGridDbGame("Hades", new[] { "Hades II" }, name => name));
        Assert.Null(OmniLibraryArtworkRules.SelectSteamGridDbGame("Hades", new[] { "Hades", "HADES" }, name => name));
        Assert.Equal(
            "The Witcher 3",
            OmniLibraryArtworkRules.SelectSteamGridDbGame(
                "The Witcher 3 Game of the Year Edition",
                new[] { "The Witcher 3", "The Witcher 3: Wild Hunt" },
                name => name));
        Assert.Equal(
            "Assassin's Creed",
            OmniLibraryArtworkRules.SelectSteamGridDbGame("Assassins Creed", new[] { "Assassin's Creed" }, name => name));
    }

    [Theory]
    [InlineData(600, 900, "Portrait", true)]
    [InlineData(300, 450, "Portrait", false)]
    [InlineData(1920, 620, "Hero", true)]
    [InlineData(1280, 720, "Hero", true)]
    [InlineData(960, 540, "Hero", false)]
    [InlineData(920, 430, "Capsule", true)]
    [InlineData(460, 215, "Capsule", false)]
    public void Quality_RequiresTargetResolutionAfterCrop(
        int width,
        int height,
        string orientation,
        bool high)
    {
        Assert.Equal(
            high ? OmniLibraryArtworkQuality.High : OmniLibraryArtworkQuality.Low,
            OmniLibraryArtworkRules.AutomaticQuality(
                width,
                height,
                Enum.Parse<OmniLibraryArtworkOrientation>(orientation)));
    }

    [Fact]
    public void Validation_RejectsCrossOrientationImages()
    {
        Assert.False(OmniLibraryArtworkRules.MatchesOrientation(1920, 620, OmniLibraryArtworkOrientation.Portrait));
        Assert.False(OmniLibraryArtworkRules.MatchesOrientation(600, 900, OmniLibraryArtworkOrientation.Hero));
        Assert.False(OmniLibraryArtworkRules.MatchesOrientation(600, 900, OmniLibraryArtworkOrientation.Capsule));
        Assert.True(OmniLibraryArtworkRules.MatchesOrientation(460, 215, OmniLibraryArtworkOrientation.Capsule));
        Assert.False(OmniLibraryArtworkRules.IsUsableFallback(512, 512, OmniLibraryArtworkOrientation.Logo));
        Assert.True(
            OmniLibraryArtworkRules.AutomaticScore(512, 512, OmniLibraryArtworkOrientation.Portrait) >
            OmniLibraryArtworkRules.AutomaticScore(3840, 2160, OmniLibraryArtworkOrientation.Portrait));
    }

    [Fact]
    public void Libretro_PrefersMatchingRegionAndKeepsHacksApart()
    {
        string[] files =
        [
            "Super Mario World (USA).png",
            "Super Mario World (Europe) (Rev 1).png",
            "Legend of Zelda, The - A Link to the Past (USA).png",
        ];

        Assert.Equal(
            "Super Mario World (Europe) (Rev 1).png",
            OmniLibraryArtworkRules.MatchLibretroThumbnail(files, "Super Mario World", "Super Mario World (Europe)", "en"));
        Assert.Equal(
            "Super Mario World (USA).png",
            OmniLibraryArtworkRules.MatchLibretroThumbnail(files, "Super Mario World", null, "en"));
        Assert.Equal(
            "Legend of Zelda, The - A Link to the Past (USA).png",
            OmniLibraryArtworkRules.MatchLibretroThumbnail(files, "The Legend of Zelda - A Link to the Past", null, "en"));
        Assert.Null(OmniLibraryArtworkRules.MatchLibretroThumbnail(
            files,
            "Super Mario World",
            "Super Mario World (Kaizo Hack)",
            "en"));
        Assert.Equal(
            ["Super Mario World (USA).png"],
            OmniLibraryArtworkDiscovery.ParseLibretroIndex(
                "<a href=\"Super%20Mario%20World%20%28USA%29.png\">x</a><a href=\"..%2Fevil.png\">y</a>"));
    }

    [Fact]
    public void GameTdb_MatchesNoIntroAndDisplayTitles()
    {
        Assert.Equal(
            OmniLibraryArtworkRules.CompactCatalogTitleKey("The Legend of Zelda: Twilight Princess"),
            OmniLibraryArtworkRules.CompactCatalogTitleKey("Legend of Zelda, The - Twilight Princess (USA)"));
        var database = OmniLibraryArtworkDiscovery.ParseGameTdbDatabase(
            "TITLES = https://www.gametdb.com\nRZDE01 = The Legend of Zelda: Twilight Princess\nRZDP01 = The Legend of Zelda: Twilight Princess\n");
        Assert.Equal(
            ["RZDE01", "RZDP01"],
            database.ByTitle[OmniLibraryArtworkRules.CompactCatalogTitleKey("Legend of Zelda, The - Twilight Princess")]);
    }

    [Fact]
    public void GameTdb_ReadsDiscIdFromImageHeader()
    {
        var root = CreateTempRoot();
        try
        {
            var path = Path.Combine(root, "game.iso");
            var bytes = new byte[0x100];
            "GALE01"u8.ToArray().CopyTo(bytes, 0);
            File.WriteAllBytes(path, bytes);
            Assert.Equal("GALE01", OmniLibraryArtworkDiscovery.DiscImageId(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SteamLibraryMatch_FindsContentHashedCacheFolders()
    {
        var root = CreateTempRoot();
        try
        {
            var steamApps = Path.Combine(root, "steamapps");
            var hashed = Path.Combine(root, "appcache", "librarycache", "515151", "0123456789abcdef");
            Directory.CreateDirectory(steamApps);
            File.WriteAllText(
                Path.Combine(steamApps, "appmanifest_515151.acf"),
                "\"AppState\"\n{\n\t\"appid\"\t\"515151\"\n\t\"name\"\t\"Hashed Cache Game™\"\n}");
            WriteTestImage(Path.Combine(hashed, "library_600x900.jpg"), 600, 900, ImageFormat.Jpeg);

            var resolver = new OmniLibraryLocalArtworkResolver(root);
            var target = new StoreSyncArtworkTarget(
                "epic-games:hashed",
                "Hashed Cache Game",
                3000000101,
                ["Hashed Cache Game"],
                null,
                string.Empty,
                "epic-games");

            var matches = resolver.ResolveSteamLibraryMatch(target);

            Assert.Equal(
                Path.Combine(hashed, "library_600x900.jpg"),
                Assert.Single(matches[OmniLibraryArtworkSlotKind.Portrait]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Pipeline_RejectsUnusableProviderArtAndKeepsRealProportions()
    {
        var root = CreateTempRoot();
        try
        {
            const uint appId = 3000000102;
            var gridDirectory = Path.Combine(root, "grid");
            var tinyPortrait = Path.Combine(root, "tiny.png");
            var hero = Path.Combine(root, "hero.png");
            WriteTestImage(tinyPortrait, 64, 96, ImageFormat.Png);
            WriteTestImage(hero, 3840, 1240, ImageFormat.Png);
            var target = new StoreSyncArtworkTarget(
                "rom-library:rules-test",
                "Zz Artwork Rules Test Without Any Remote Match",
                appId,
                ["Zz Artwork Rules Test Without Any Remote Match"],
                null,
                string.Empty,
                OmniLibraryRomSystemRegistry.StoreId,
                new Uri(tinyPortrait).AbsoluteUri,
                new Uri(hero).AbsoluteUri);

            await new OmniLibraryArtworkPipeline().DownloadLocalFirstAsync(
                gridDirectory,
                [target],
                string.Empty,
                CancellationToken.None);

            var gridId = SteamShortcutIds.BuildGridId(appId);
            using (var heroImage = Image.FromFile(Path.Combine(gridDirectory, $"{gridId}_hero.png")))
            {
                // Downscaled to ORBIT's display cap, never cropped to 1920x620.
                Assert.Equal(1920, heroImage.Width);
                Assert.Equal(620, heroImage.Height);
            }
            using (var portraitImage = Image.FromFile(Directory.GetFiles(gridDirectory, $"{gridId}p.*").Single()))
            {
                // The 64x96 thumbnail was rejected; the wide hero became a
                // portrait fallback at Steam's slot size.
                Assert.Equal(600, portraitImage.Width);
                Assert.Equal(900, portraitImage.Height);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamloader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteTestImage(string path, int width, int height, ImageFormat format)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.DarkSlateBlue);
        bitmap.Save(path, format);
    }
}
