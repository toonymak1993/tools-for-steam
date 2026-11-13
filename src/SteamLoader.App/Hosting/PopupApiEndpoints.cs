using System.Net;
using System.Text.Json;
using SteamLoader.App.Infrastructure.Popups;

namespace SteamLoader.App.Hosting;

/// <summary>
/// <c>/api/popups/*</c> for TFS Popups. The API server dispatches here after
/// its token and feature checks, so a disabled feature answers 403 and
/// popups-core.js leaves every notification to Steam.
/// </summary>
internal static class PopupApiEndpoints
{
    public const string RoutePrefix = "/api/popups";

    private const int MaxBodyBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool Handles(string? path) =>
        path is not null &&
        (path.Equals(RoutePrefix, StringComparison.Ordinal) ||
         path.StartsWith(RoutePrefix + "/", StringComparison.Ordinal));

    /// <summary>Returns false when the request is not a popups route.</summary>
    public static async Task<bool> TryHandleAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        PopupService? service,
        CancellationToken cancellationToken)
    {
        var path = request.Url?.AbsolutePath;
        if (!Handles(path))
        {
            return false;
        }

        if (service is null)
        {
            await WriteAsync(response, HttpStatusCode.ServiceUnavailable, new { message = "TFS Popups is not available." }, cancellationToken);
            return true;
        }

        if (request.ContentLength64 > MaxBodyBytes)
        {
            await WriteAsync(response, HttpStatusCode.RequestEntityTooLarge, new { message = "The popup is too large." }, cancellationToken);
            return true;
        }

        var isGet = request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase);
        var isPost = request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase);
        try
        {
            switch (path)
            {
                case "/api/popups/state" when isGet:
                    await WriteAsync(response, HttpStatusCode.OK, service.GetState(), cancellationToken);
                    return true;

                case "/api/popups/settings" when isPost:
                {
                    var update = await ReadAsync<PopupSettingsUpdate>(request, cancellationToken);
                    if (update is null)
                    {
                        await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "Popup settings are required." }, cancellationToken);
                        return true;
                    }

                    await WriteAsync(response, HttpStatusCode.OK, service.SaveSettings(update), cancellationToken);
                    return true;
                }

                case "/api/popups/show" when isPost:
                {
                    var popup = await ReadAsync<PopupRequest>(request, cancellationToken);
                    if (popup is null)
                    {
                        await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "A popup is required." }, cancellationToken);
                        return true;
                    }

                    await WriteAsync(response, HttpStatusCode.OK, await service.ShowAsync(popup, cancellationToken), cancellationToken);
                    return true;
                }

                case "/api/popups/volume-preview" when isPost:
                    service.ShowVolumePreview();
                    await WriteAsync(response, HttpStatusCode.OK, new { shown = true }, cancellationToken);
                    return true;
            }

            await WriteAsync(response, HttpStatusCode.NotFound, new { message = "Unknown popups route." }, cancellationToken);
            return true;
        }
        catch (JsonException)
        {
            await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "The request body is not valid JSON." }, cancellationToken);
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
}
