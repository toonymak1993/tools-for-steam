using System.Net;
using System.Text.Json;
using SteamLoader.App.Infrastructure.Trailers;

namespace SteamLoader.App.Hosting;

/// <summary>
/// <c>/api/trailers/*</c> for TFS Trailers. Kept out of the big API server
/// file; the server only dispatches here (after its token and plugin checks).
/// </summary>
internal static class TrailerApiEndpoints
{
    public const string RoutePrefix = "/api/trailers";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool Handles(string? path) =>
        path is not null &&
        (path.Equals(RoutePrefix, StringComparison.Ordinal) ||
         path.StartsWith(RoutePrefix + "/", StringComparison.Ordinal));

    /// <summary>Returns false when the request is not a trailer route.</summary>
    public static async Task<bool> TryHandleAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        TrailerService? service,
        CancellationToken cancellationToken)
    {
        var path = request.Url?.AbsolutePath;
        if (!Handles(path))
        {
            return false;
        }

        if (service is null)
        {
            await WriteAsync(response, HttpStatusCode.ServiceUnavailable, new { message = "TFS Trailers is not available." }, cancellationToken);
            return true;
        }

        var isGet = request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase);
        var isPost = request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase);
        try
        {
            switch (path)
            {
                case "/api/trailers/settings" when isGet:
                    await WriteAsync(response, HttpStatusCode.OK, service.GetSettings(), cancellationToken);
                    return true;

                case "/api/trailers/settings" when isPost:
                {
                    var update = await ReadAsync<TrailerGlobalUpdate>(request, cancellationToken);
                    if (update is null)
                    {
                        await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "Trailer settings are required." }, cancellationToken);
                        return true;
                    }

                    await WriteAsync(response, HttpStatusCode.OK, service.SaveSettings(update), cancellationToken);
                    return true;
                }

                case "/api/trailers/game" when isGet:
                {
                    if (!uint.TryParse(request.QueryString["appId"], out var appId) || appId == 0)
                    {
                        await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "A valid appId is required." }, cancellationToken);
                        return true;
                    }

                    var snapshot = await service.GetGameAsync(
                        appId,
                        request.QueryString["title"],
                        request.QueryString["shortcut"] is "1" or "true",
                        cancellationToken);
                    await WriteAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                    return true;
                }

                case "/api/trailers/game" or "/api/trailers/game/reset" when isPost:
                {
                    var payload = await ReadAsync<SaveTrailerGameRequest>(request, cancellationToken);
                    if (payload is null || payload.AppId == 0)
                    {
                        await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "A valid appId is required." }, cancellationToken);
                        return true;
                    }

                    if (path.EndsWith("/reset", StringComparison.Ordinal))
                    {
                        service.ResetGame(payload.AppId);
                    }
                    else
                    {
                        service.SaveGame(payload.AppId, payload.Settings ?? new TrailerGameSettings());
                    }

                    var snapshot = await service.GetGameAsync(payload.AppId, payload.Title, payload.IsShortcut, cancellationToken);
                    await WriteAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                    return true;
                }

                case "/api/trailers/search" when isGet:
                {
                    var results = await service.SearchStoreAsync(request.QueryString["term"] ?? string.Empty, cancellationToken);
                    await WriteAsync(response, HttpStatusCode.OK, new { items = results }, cancellationToken);
                    return true;
                }
            }

            await WriteAsync(response, HttpStatusCode.NotFound, new { message = "Unknown trailer route." }, cancellationToken);
            return true;
        }
        catch (JsonException)
        {
            await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "The request body is not valid JSON." }, cancellationToken);
            return true;
        }
        catch (HttpRequestException)
        {
            await WriteAsync(response, HttpStatusCode.BadGateway, new { message = "Steam could not be reached." }, cancellationToken);
            return true;
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpListenerRequest request, CancellationToken cancellationToken) =>
        await JsonSerializer.DeserializeAsync<T>(request.InputStream, JsonOptions, cancellationToken);

    private static async Task WriteAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        object payload,
        CancellationToken cancellationToken)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        await using var output = response.OutputStream;
        await JsonSerializer.SerializeAsync(output, payload, payload.GetType(), JsonOptions, cancellationToken);
    }

    internal sealed record SaveTrailerGameRequest(uint AppId, TrailerGameSettings? Settings, string? Title, bool IsShortcut);
}
