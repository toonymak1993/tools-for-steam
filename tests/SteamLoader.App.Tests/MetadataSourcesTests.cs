using System.Text.Json;
using SteamLoader.App.Infrastructure.Metadata;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class MetadataSourcesTests
{
    [Theory]
    [InlineData("Hollow Knight", "hollow-knight")]
    [InlineData("Assassin's Creed® Odyssey™", "assassins-creed-odyssey")]
    [InlineData("The Witcher 3: Wild Hunt", "the-witcher-3-wild-hunt")]
    [InlineData("Pokémon Legends: Arceus", "pokemon-legends-arceus")]
    [InlineData("Ratchet & Clank", "ratchet-and-clank")]
    public void Slugify_FollowsIgnPageAddresses(string title, string slug)
    {
        Assert.Equal(slug, IgnMetadataClient.Slugify(title));
    }

    [Fact]
    public void CandidateSlugs_AlsoTriesWithoutLeadingThe()
    {
        Assert.Equal(
            ["the-witcher-3", "witcher-3"],
            IgnMetadataClient.CandidateSlugs("The Witcher 3").ToArray());
    }

    [Theory]
    [InlineData("https://www.ign.com/games/hollow-knight", "hollow-knight")]
    [InlineData("hollow-knight", "hollow-knight")]
    [InlineData("https://example.com/games/hollow-knight", null)]
    [InlineData("../etc", null)]
    public void ParseSlug_AcceptsIgnUrlsAndSlugsOnly(string value, string? expected)
    {
        Assert.Equal(expected, IgnMetadataClient.ParseSlug(value));
    }

    [Fact]
    public void Parse_ReadsGameInfoAndRejectsNonGamePages()
    {
        var info = IgnMetadataClient.Parse(
            OmniLibraryGamePageMetadataServiceTests.IgnPage("Hollow Knight"),
            "hollow-knight");

        Assert.NotNull(info);
        Assert.Equal("Hollow Knight", info.Name);
        Assert.Equal("A long IGN description.", info.Description);
        Assert.Equal(["Action", "Platformer"], info.Genres);
        Assert.Equal(94, info.Rating);
        Assert.Null(IgnMetadataClient.Parse("<html>no data</html>", "x"));
        Assert.Null(IgnMetadataClient.Parse(
            """<script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"page":{"type":"Article","name":"News"}}}}</script>""",
            "x"));
    }

    [Theory]
    [InlineData("Энтузиаст запустил Halo: Combat Evolved на Sega Dreamcast", "de", false)]
    [InlineData("Энтузиаст запустил Halo: Combat Evolved на Sega Dreamcast", "ru", true)]
    [InlineData("Halo: Campaign Evolved Update 1.2", "de", true)]
    [InlineData("ヘイロー 最新情報", "en", false)]
    public void IsReadableNewsTitle_DropsForeignScriptFeeds(string title, string language, bool expected)
    {
        Assert.Equal(
            expected,
            SteamLoader.App.Infrastructure.StoreSync.OmniLibraryGamePageMetadataService.IsReadableNewsTitle(title, language));
    }

    [Fact]
    public void ParseGoogleResponse_JoinsTranslatedSegments()
    {
        using var document = JsonDocument.Parse(
            """[[["Hallo ","Hello ",null],["Welt.","World.",null]],null,"en"]""");
        Assert.Equal("Hallo Welt.", MetadataTranslator.ParseGoogleResponse(document.RootElement));
    }

    [Fact]
    public void SplitIntoChunks_CutsAtSentenceEndsWithinTheLimit()
    {
        var text = string.Join(" ", Enumerable.Repeat("This is one sentence.", 60));
        var chunks = MetadataTranslator.SplitIntoChunks(text, 450);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 450));
        Assert.All(chunks, chunk => Assert.EndsWith(".", chunk));
        Assert.Equal(text, string.Join(" ", chunks));
    }
}
