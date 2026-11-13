using System.Net;
using System.Text.Json;
using SteamLoader.App.Infrastructure.Artwork;

namespace SteamLoader.App.Hosting;

/// <summary>
/// <c>/api/artwork/picker/*</c> for the TFS Artworks page. Search, apply and
/// settings keep using the existing <c>/api/artwork/*</c> routes.
/// </summary>
internal static class ArtworkPickerApiEndpoints
{
    public const string RoutePrefix = "/api/artwork/picker";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool Handles(string? path) =>
        path is not null && path.StartsWith(RoutePrefix + "/", StringComparison.Ordinal);

    public static async Task<bool> TryHandleAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        ArtworkPickerService? service,
        CancellationToken cancellationToken)
    {
        var path = request.Url?.AbsolutePath;
        if (!Handles(path))
        {
            return false;
        }

        if (service is null)
        {
            await WriteAsync(response, HttpStatusCode.ServiceUnavailable, new { message = "The artwork page is not available." }, cancellationToken);
            return true;
        }

        var isGet = request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase);
        var isPost = request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase);
        try
        {
            switch (path)
            {
                case "/api/artwork/picker/game" when isGet:
                {
                    if (!long.TryParse(request.QueryString["appId"], out var appId) || appId <= 0)
                    {
                        await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "A valid appId is required." }, cancellationToken);
                        return true;
                    }

                    var game = await service.GetGameAsync(
                        appId,
                        request.QueryString["title"],
                        request.QueryString["shortcut"] is "1" or "true",
                        cancellationToken);
                    await WriteAsync(response, HttpStatusCode.OK, game, cancellationToken);
                    return true;
                }

                case "/api/artwork/picker/assets" when isGet:
                {
                    _ = int.TryParse(request.QueryString["gameId"], out var gameId);
                    _ = int.TryParse(request.QueryString["page"], out var page);
                    var type = request.QueryString["type"] ?? "grid_p";
                    var filter = new ArtworkAssetFilter(
                        SplitList(request.QueryString["styles"]),
                        request.QueryString["animation"],
                        request.QueryString["nsfw"] is "1" or "true",
                        request.QueryString["humor"] is "1" or "true",
                        SplitList(request.QueryString["dimensions"]));
                    var assets = await service.SteamGridDb.SearchPickerAssetsAsync(gameId, type, filter, page, cancellationToken);
                    await WriteAsync(response, HttpStatusCode.OK, new { items = assets }, cancellationToken);
                    return true;
                }

                case "/api/artwork/picker/match" when isPost:
                {
                    var payload = await ReadAsync<SaveMatchRequest>(request, cancellationToken);
                    if (payload is null || payload.AppId <= 0)
                    {
                        await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "A valid appId is required." }, cancellationToken);
                        return true;
                    }

                    service.SaveMatch(payload.AppId, payload.GameId, payload.Name);
                    await WriteAsync(response, HttpStatusCode.OK, new { saved = true }, cancellationToken);
                    return true;
                }

                case "/api/artwork/picker/compose" when isPost:
                {
                    var payload = await ReadAsync<ComposeRequest>(request, cancellationToken);
                    if (payload is null || payload.AppId <= 0 || payload.Kind is not ("hero" or "grid_l") ||
                        string.IsNullOrWhiteSpace(payload.BackgroundUrl) || string.IsNullOrWhiteSpace(payload.LogoUrl))
                    {
                        await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "A game, a background and a logo are required." }, cancellationToken);
                        return true;
                    }

                    var backgroundTask = service.SteamGridDb.DownloadImageAsync(payload.BackgroundUrl, cancellationToken);
                    var logoTask = service.SteamGridDb.DownloadImageAsync(payload.LogoUrl, cancellationToken);
                    var background = await backgroundTask;
                    var logo = await logoTask;
                    if (background is null || logo is null)
                    {
                        await WriteAsync(response, HttpStatusCode.BadGateway, new { message = background is null ? "The background could not be downloaded." : "The logo could not be downloaded." }, cancellationToken);
                        return true;
                    }

                    byte[] png;
                    try
                    {
                        png = await StaThread.RunAsync(
                            () => PerfectArtworkComposer.Compose(payload.Kind, background, logo, payload.Layout ?? new PerfectArtworkLayout()),
                            cancellationToken);
                    }
                    catch (Exception exception) when (exception is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
                    {
                        await WriteAsync(response, HttpStatusCode.UnprocessableEntity, new { message = "One of the images could not be decoded (animated or unsupported format)." }, cancellationToken);
                        return true;
                    }

                    var name = payload.Kind == "hero" ? "Perfect Hero" : "Perfect Banner";
                    var result = service.SteamGridDb.WriteGeneratedArtwork(payload.AppId, payload.Kind, png, $"{name} created.");
                    await WriteAsync(response, result.Success ? HttpStatusCode.OK : HttpStatusCode.BadRequest, result, cancellationToken);
                    return true;
                }

                case "/api/artwork/picker/restore" when isPost:
                {
                    var payload = await ReadAsync<RestoreRequest>(request, cancellationToken);
                    if (payload is null || payload.AppId <= 0 ||
                        !SteamGridDbManualArtworkService.TryGetSteamAssetType(payload.Type, out var steamAssetType))
                    {
                        await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "A valid appId and artwork type are required." }, cancellationToken);
                        return true;
                    }

                    var removed = service.SteamGridDb.RemoveGridArtwork(payload.AppId, payload.Type!);
                    await WriteAsync(response, HttpStatusCode.OK, new { removed, steamAssetType }, cancellationToken);
                    return true;
                }
            }

            await WriteAsync(response, HttpStatusCode.NotFound, new { message = "Unknown artwork page route." }, cancellationToken);
            return true;
        }
        catch (JsonException)
        {
            await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "The request body is not valid JSON." }, cancellationToken);
            return true;
        }
        catch (HttpRequestException)
        {
            await WriteAsync(response, HttpStatusCode.BadGateway, new { message = "SteamGridDB or Steam could not be reached." }, cancellationToken);
            return true;
        }
    }

    internal static IReadOnlyList<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(12).ToArray();

    private static async Task<T?> ReadAsync<T>(HttpListenerRequest request, CancellationToken cancellationToken) =>
        await JsonSerializer.DeserializeAsync<T>(request.InputStream, JsonOptions, cancellationToken);

    private static async Task WriteAsync(HttpListenerResponse response, HttpStatusCode statusCode, object payload, CancellationToken cancellationToken)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        await using var output = response.OutputStream;
        await JsonSerializer.SerializeAsync(output, payload, payload.GetType(), JsonOptions, cancellationToken);
    }

    internal sealed record SaveMatchRequest(long AppId, int GameId, string? Name);

    internal sealed record RestoreRequest(long AppId, string? Type);

    internal sealed record ComposeRequest(long AppId, string Kind, string BackgroundUrl, string LogoUrl, PerfectArtworkLayout? Layout);
}
