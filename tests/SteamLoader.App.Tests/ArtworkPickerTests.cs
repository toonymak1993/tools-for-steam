using SteamLoader.App.Hosting;
using SteamLoader.App.Infrastructure.Artwork;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class ArtworkPickerTests
{
    [Fact]
    public void OfficialSteamAssetsMapToArtworkTypes()
    {
        const string json = """
            {"response":{"store_items":[{"assets":{
              "asset_url_format":"steam/apps/1808500/${FILENAME}?t=1",
              "header":"41ee/header.jpg","header_2x":"41ee/header_2x.jpg",
              "library_capsule":"e33d/library_600x900.jpg","library_capsule_2x":"e33d/library_600x900_2x.jpg",
              "library_hero":"ab19/library_hero.jpg","library_hero_2x":"ab19/library_hero_2x.jpg",
              "main_capsule":"a602/capsule_616x353.jpg","community_icon":"c284e73b",
              "page_background":"../evil.jpg"
            }}]}}
            """;

        var assets = SteamOfficialArtworkService.Parse(json, 1808500);

        Assert.Equal(
            "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1808500/e33d/library_600x900_2x.jpg?t=1",
            assets["grid_p"][0].Url);
        Assert.Equal(1200, assets["grid_p"][0].Width);
        Assert.Equal(["header_2x", "header", "main_capsule"], assets["grid_l"].Select(asset => asset.Id.Replace("steam:", string.Empty)));
        Assert.Equal(3840, assets["hero"][0].Width);
        Assert.Empty(assets["logo"]); // GetItems has no library logo; no guessed path
        Assert.EndsWith("/1808500/c284e73b.jpg", assets["icon"][0].Url);
        Assert.All(assets.Values.SelectMany(list => list), asset => Assert.Equal("steam", asset.Source));
        Assert.Empty(SteamOfficialArtworkService.Parse("nope", 1)["grid_p"]);
    }

    [Fact]
    public void SteamGridDbQueriesOnlyUseKnownStylesAndSizes()
    {
        var path = SteamGridDbManualArtworkService.BuildPickerAssetPath(
            77,
            "grid_p",
            new ArtworkAssetFilter(["material", "evil", "MATERIAL"], "animated", AllowNsfw: true, Dimensions: ["512x512", "9999x1"]),
            1);

        Assert.StartsWith("grids/game/77?dimensions=512x512&styles=material&types=animated&", path);
        Assert.Contains("nsfw=any", path);
        Assert.Contains("humor=false", path);
        Assert.EndsWith("page=1", path);

        var defaults = SteamGridDbManualArtworkService.BuildPickerAssetPath(5, "hero", null, 0);
        Assert.Equal(
            "heroes/game/5?dimensions=1920x620,3840x1240,1600x650&types=static,animated&mimes=image/png,image/jpeg,image/webp&epilepsy=false&nsfw=false&humor=false&page=0",
            defaults);

        var logos = SteamGridDbManualArtworkService.BuildPickerAssetPath(5, "logo", new ArtworkAssetFilter(["white"]), 0);
        Assert.StartsWith("logos/game/5?styles=white&types=static,animated", logos);
    }

    [Fact]
    public void PerfectArtworkKeepsSteamShapesAndLogoInside()
    {
        Assert.Equal((1920, 620), PerfectArtworkComposer.TargetSize("hero", 1280));
        Assert.Equal((3840, 1240), PerfectArtworkComposer.TargetSize("hero", 5000));
        Assert.Equal((920, 430), PerfectArtworkComposer.TargetSize("grid_l", 1600));
        Assert.Equal((1840, 860), PerfectArtworkComposer.TargetSize("grid_l", 3840));

        var rect = PerfectArtworkComposer.LogoRect(1920, 620, 800, 400, new PerfectArtworkLayout(LogoX: 0, LogoY: 100, LogoWidth: 40));
        Assert.Equal(0, rect.Left);
        Assert.Equal(620 - rect.Height, rect.Top, 3);
        Assert.Equal(768, rect.Width, 3);

        var tall = PerfectArtworkComposer.LogoRect(920, 430, 100, 400, new PerfectArtworkLayout(LogoWidth: 80));
        Assert.True(tall.Height <= 430 * 0.9 + 0.001);
    }

    [Fact]
    public void PerfectHeroBakesTheLogoIntoTheBackground()
    {
        byte[]? png = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var background = SolidPng(1920, 620, System.Windows.Media.Colors.Navy);
                var logo = SolidPng(400, 200, System.Windows.Media.Colors.Red);
                png = PerfectArtworkComposer.Compose("hero", background, logo, new PerfectArtworkLayout(LogoX: 50, LogoY: 50, LogoWidth: 25, Shadow: 0));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        using var stream = new MemoryStream(png!);
        var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(1920, frame.PixelWidth);
        Assert.Equal(620, frame.PixelHeight);
        var pixel = new byte[4];
        frame.CopyPixels(new System.Windows.Int32Rect(960, 310, 1, 1), pixel, 4, 0);
        Assert.True(pixel[2] > 200 && pixel[0] < 60, "the centre shows the red logo"); // BGRA
        frame.CopyPixels(new System.Windows.Int32Rect(20, 20, 1, 1), pixel, 4, 0);
        Assert.True(pixel[0] > 100 && pixel[2] < 60, "the corner shows the navy background");
    }

    private static byte[] SolidPng(int width, int height, System.Windows.Media.Color color)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = color.B;
            pixels[index + 1] = color.G;
            pixels[index + 2] = color.R;
            pixels[index + 3] = 255;
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    [Fact]
    public void RestoreKnowsSteamAssetTypes()
    {
        Assert.True(SteamGridDbManualArtworkService.TryGetSteamAssetType("hero", out var hero));
        Assert.Equal(1, hero);
        Assert.True(SteamGridDbManualArtworkService.TryGetSteamAssetType("grid_l", out var wide));
        Assert.Equal(3, wide);
        Assert.False(SteamGridDbManualArtworkService.TryGetSteamAssetType("banner", out _));
        Assert.Equal(["a", "b"], ArtworkPickerApiEndpoints.SplitList(" a, ,b "));
        Assert.Empty(ArtworkPickerApiEndpoints.SplitList(null));
    }
}
