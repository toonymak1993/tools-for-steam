using System.Text.Json;
using SteamLoader.App.Infrastructure.Hltb;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class HltbServiceTests
{
    [Fact]
    public void ExtractSearchEndpointReadsTheInitCallFromTheSiteBundle()
    {
        const string script =
            "er=async()=>{try{let e=await fetch(`/api/search/site/init?t=${Date.now()}`);if(e.ok){}}catch(e){}}";

        Assert.Equal("/api/search/site", HltbService.ExtractSearchEndpoint(script));
        Assert.Equal("/api/bleed", HltbService.ExtractSearchEndpoint("fetch(\"/api/bleed/init?t=\"+Date.now())"));
        Assert.Null(HltbService.ExtractSearchEndpoint("fetch(\"/api/user\")"));
    }

    [Fact]
    public void ExtractScriptPathsFindsNextChunks()
    {
        const string html =
            "<script src=\"/_next/static/chunks/0ovmds8czs1lw.js\"></script><script src=\"/_next/static/chunks/turbopack-2c3mg3r-05n1w.js\"></script><script src=\"/_next/static/chunks/0ovmds8czs1lw.js\"></script>";

        Assert.Equal(
            ["/_next/static/chunks/0ovmds8czs1lw.js", "/_next/static/chunks/turbopack-2c3mg3r-05n1w.js"],
            HltbService.ExtractScriptPaths(html).ToArray());
    }

    [Fact]
    public void SearchTitlesDropTrademarksAndEditionSuffixes()
    {
        Assert.Equal(
            ["Batman: Arkham Knight"],
            HltbService.BuildSearchTitles("Batman™: Arkham Knight"));
        Assert.Equal(
            ["DOOM Eternal - Deluxe Edition", "DOOM Eternal"],
            HltbService.BuildSearchTitles("DOOM Eternal – Deluxe Edition"));
        Assert.Equal(
            ["The Witcher 3: Wild Hunt - Game of the Year Edition", "The Witcher 3: Wild Hunt"],
            HltbService.BuildSearchTitles("The Witcher® 3: Wild Hunt - Game of the Year Edition"));
    }

    [Fact]
    public void SelectBestMatchPrefersExactNamesAndRejectsUnrelatedGames()
    {
        var results = new List<HltbService.HltbSearchResult>
        {
            new() { GameId = 2, GameName = "Hades II", CompAllCount = 10 },
            new() { GameId = 1, GameName = "Hades", CompAllCount = 5 },
        };

        Assert.Equal(1, HltbService.SelectBestMatch("Hades", 1145360, results)?.GameId);
        Assert.Equal(2, HltbService.SelectBestMatch("Hades 2", null, [
            new() { GameId = 2, GameName = "Hades II", GameAlias = "Hades 2" },
        ])?.GameId);
        Assert.Null(HltbService.SelectBestMatch("Hades 2", null, [
            new() { GameId = 2, GameName = "Hades II" },
        ], allowFuzzy: false));
        Assert.Null(HltbService.SelectBestMatch("Stardew Valley", null, [
            new() { GameId = 9, GameName = "Valley of the Kings: Reborn Collection" },
        ]));
    }

    [Fact]
    public void SearchPayloadUsesCurrentFilterShapeAndOnlyAddsLegacyHoneypotWhenGiven()
    {
        var payload = HltbService.BuildSearchPayload("Hades II", new HltbService.HltbSearchInitResponse { Token = "t" });
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;
        Assert.Equal(["Hades", "II"], root.GetProperty("searchTerms").EnumerateArray().Select(item => item.GetString()!).ToArray());
        var games = root.GetProperty("searchOptions").GetProperty("games");
        Assert.Equal("include", games.GetProperty("platform").GetProperty("mode").GetString());
        Assert.Equal("include", games.GetProperty("gameplay").GetProperty("genre").GetProperty("mode").GetString());
        Assert.Equal("include", games.GetProperty("year").GetProperty("mode").GetString());
        Assert.Equal(6, root.EnumerateObject().Count());

        var legacy = HltbService.BuildSearchPayload("Hades", new HltbService.HltbSearchInitResponse { Token = "t", HpKey = "abc", HpVal = "def" });
        Assert.Equal("def", legacy["abc"]);
    }
}
