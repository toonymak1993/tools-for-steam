using System.Net;
using System.Text.Json;
using SteamLoader.App.Infrastructure.Discord;

namespace SteamLoader.App.Hosting;

/// <summary>
/// <c>/api/discord/web/*</c> for Discord Web in Big Picture (tfs-discord.mjs).
/// Dispatched after the token and plugin checks, so turning the Discord
/// integration off also blocks these routes.
/// </summary>
internal static class DiscordWebApiEndpoints
{
    public const string RoutePrefix = "/api/discord/web";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool Handles(string? path) =>
        path is not null &&
        (path.Equals(RoutePrefix, StringComparison.Ordinal) ||
         path.StartsWith(RoutePrefix + "/", StringComparison.Ordinal));

    public static async Task<bool> TryHandleAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        DiscordWebBridge? bridge,
        CancellationToken cancellationToken)
    {
        var path = request.Url?.AbsolutePath;
        if (!Handles(path))
        {
            return false;
        }

        if (bridge is null)
        {
            await WriteAsync(response, HttpStatusCode.ServiceUnavailable, new { message = "Discord Web is not available." }, cancellationToken);
            return true;
        }

        var isGet = request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase);
        var isPost = request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase);
        try
        {
            switch (path)
            {
                case "/api/discord/web/state" when isGet:
                    await WriteAsync(response, HttpStatusCode.OK, await bridge.GetStateAsync(cancellationToken), cancellationToken);
                    return true;

                case "/api/discord/web/click" when isPost:
                {
                    var body = await ReadAsync<DiscordWebClickRequest>(request, cancellationToken);
                    await WriteResultAsync(response, await bridge.ClickAsync(body?.Button, cancellationToken), cancellationToken);
                    return true;
                }

                case "/api/discord/web/action" when isPost:
                {
                    var body = await ReadAsync<DiscordWebActionRequest>(request, cancellationToken);
                    await WriteResultAsync(response, await bridge.RunShortcutAsync(body?.Action, cancellationToken), cancellationToken);
                    return true;
                }

                case "/api/discord/web/back" when isPost:
                    await WriteAsync(response, HttpStatusCode.OK, await bridge.BackAsync(cancellationToken), cancellationToken);
                    return true;

                case "/api/discord/web/navigate" when isPost:
                {
                    var body = await ReadAsync<DiscordWebNavigateRequest>(request, cancellationToken);
                    await WriteResultAsync(response, await bridge.NavigateAsync(body?.Path, cancellationToken), cancellationToken);
                    return true;
                }
            }

            await WriteAsync(response, HttpStatusCode.NotFound, new { message = "Unknown Discord Web route." }, cancellationToken);
            return true;
        }
        catch (JsonException)
        {
            await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "The request body is not valid JSON." }, cancellationToken);
            return true;
        }
    }

    private static Task WriteResultAsync(
        HttpListenerResponse response,
        DiscordWebActionResult result,
        CancellationToken cancellationToken) =>
        WriteAsync(response, result.Success ? HttpStatusCode.OK : HttpStatusCode.Conflict, result, cancellationToken);

    private static async Task<T?> ReadAsync<T>(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasEntityBody)
        {
            return default;
        }

        return await JsonSerializer.DeserializeAsync<T>(request.InputStream, JsonOptions, cancellationToken);
    }

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

    private sealed record DiscordWebClickRequest(string? Button);

    private sealed record DiscordWebActionRequest(string? Action);

    private sealed record DiscordWebNavigateRequest(string? Path);
}
