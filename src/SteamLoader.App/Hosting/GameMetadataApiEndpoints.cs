using System.Diagnostics;
using System.Net;
using System.Text.Json;
using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Models;

namespace SteamLoader.App.Hosting;

/// <summary>
/// <c>/api/metadata/*</c>: game page metadata for every non-Steam shortcut.
/// OmniLibrary games resolve to their OmniLibrary identity inside the service,
/// so OmniLibrary keeps control of their achievements; every other shortcut is
/// matched by the title Steam shows for it. The API server dispatches here
/// after its token and feature checks.
/// </summary>
internal static class GameMetadataApiEndpoints
{
    public const string RoutePrefix = "/api/metadata";

    private const string GamesPrefix = RoutePrefix + "/games/";
    private const string MatchSuffix = "/match";
    private const string EditSuffix = "/edit";
    private const string IgnSuffix = "/ign";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool Handles(string? path) =>
        path is not null &&
        (path.Equals(RoutePrefix, StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith(RoutePrefix + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns false when the request is not a metadata route.</summary>
    public static async Task<bool> TryHandleAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        OmniLibraryGamePageMetadataService? service,
        CancellationToken cancellationToken)
    {
        var path = request.Url?.AbsolutePath?.TrimEnd('/');
        if (!Handles(path))
        {
            return false;
        }

        var method = request.HttpMethod.ToUpperInvariant();
        if (path!.Equals(RoutePrefix + "/state", StringComparison.OrdinalIgnoreCase) && method == "GET")
        {
            await WriteAsync(response, HttpStatusCode.OK, new { enabled = true }, cancellationToken);
            return true;
        }

        if (path.Equals(RoutePrefix + "/open", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            OpenMetadataLinkRequest? link;
            try
            {
                link = await JsonSerializer.DeserializeAsync<OpenMetadataLinkRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
            }
            catch (JsonException)
            {
                link = null;
            }

            // Same host allowlist as the OmniLibrary route: only store and
            // news pages, opened through the shell, never arbitrary URLs.
            if (!SteamLoaderApiServer.TryNormalizeMetadataUrl(link?.Value, out var url))
            {
                await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "This metadata link is not allowed." }, cancellationToken);
                return true;
            }

            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })?.Dispose();
            await WriteAsync(response, HttpStatusCode.OK, new { message = "Opened metadata link." }, cancellationToken);
            return true;
        }

        if (service is null)
        {
            await WriteAsync(response, HttpStatusCode.ServiceUnavailable, new { message = "Game metadata is not available." }, cancellationToken);
            return true;
        }

        var title = request.QueryString["title"];
        try
        {
            if (path.Equals(RoutePrefix + "/search", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                if (!TryParseShortcutAppId(request.QueryString["appId"], out var searchAppId))
                {
                    await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "A valid appId is required." }, cancellationToken);
                    return true;
                }

                var matches = await service.SearchSteamMatchesAsync(
                    searchAppId,
                    title,
                    request.QueryString["term"],
                    cancellationToken);
                await WriteAsync(response, HttpStatusCode.OK, matches, cancellationToken);
                return true;
            }

            if (!path.StartsWith(GamesPrefix, StringComparison.OrdinalIgnoreCase))
            {
                await WriteAsync(response, HttpStatusCode.NotFound, new { message = "Unknown metadata route." }, cancellationToken);
                return true;
            }

            var rest = path[GamesPrefix.Length..];
            var isMatchRoute = rest.EndsWith(MatchSuffix, StringComparison.OrdinalIgnoreCase);
            var isEditRoute = rest.EndsWith(EditSuffix, StringComparison.OrdinalIgnoreCase);
            var isIgnRoute = rest.EndsWith(IgnSuffix, StringComparison.OrdinalIgnoreCase);
            if (isMatchRoute)
            {
                rest = rest[..^MatchSuffix.Length];
            }
            else if (isEditRoute)
            {
                rest = rest[..^EditSuffix.Length];
            }
            else if (isIgnRoute)
            {
                rest = rest[..^IgnSuffix.Length];
            }

            if (!TryParseShortcutAppId(rest, out var appId))
            {
                await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "A valid appId is required." }, cancellationToken);
                return true;
            }

            if (isIgnRoute)
            {
                if (method != "POST")
                {
                    await WriteAsync(response, HttpStatusCode.MethodNotAllowed, new { message = "Method not allowed." }, cancellationToken);
                    return true;
                }

                var payload = await JsonSerializer.DeserializeAsync<SetIgnPageRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                GameMetadataEditorSnapshot? ignEditor;
                try
                {
                    ignEditor = await service.SetIgnPageAsync(
                        appId,
                        payload?.Title ?? title,
                        payload?.Value,
                        cancellationToken);
                }
                catch (ArgumentException error)
                {
                    await WriteAsync(response, HttpStatusCode.BadRequest, new { message = error.Message }, cancellationToken);
                    return true;
                }

                await WriteAsync(
                    response,
                    ignEditor is null ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                    ignEditor is null
                        ? (object)new { message = "Metadata needs a non-Steam shortcut with a title." }
                        : ignEditor,
                    cancellationToken);
                return true;
            }

            if (isEditRoute)
            {
                GameMetadataEditorSnapshot? editor;
                if (method == "GET")
                {
                    editor = await service.GetEditorAsync(appId, title, cancellationToken);
                }
                else if (method == "POST")
                {
                    var payload = await JsonSerializer.DeserializeAsync<SaveGameMetadataEditsRequest>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken);
                    editor = await service.SaveEditsAsync(
                        appId,
                        payload?.Title ?? title,
                        payload?.Edits,
                        cancellationToken);
                }
                else if (method == "DELETE")
                {
                    editor = await service.SaveEditsAsync(appId, title, null, cancellationToken);
                }
                else
                {
                    await WriteAsync(response, HttpStatusCode.MethodNotAllowed, new { message = "Method not allowed." }, cancellationToken);
                    return true;
                }

                await WriteAsync(
                    response,
                    editor is null ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                    editor is null
                        ? (object)new { message = "Metadata needs a non-Steam shortcut with a title." }
                        : editor,
                    cancellationToken);
                return true;
            }

            OmniLibraryGamePageMetadataSnapshot? snapshot;
            if (isMatchRoute && method == "DELETE")
            {
                snapshot = await service.ClearSteamMatchAsync(appId, title, cancellationToken);
            }
            else if (isMatchRoute && method == "POST")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetGameMetadataMatchRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null || payload.SteamAppId <= 0)
                {
                    await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "A positive Steam app ID is required." }, cancellationToken);
                    return true;
                }

                snapshot = await service.SetSteamMatchAsync(
                    appId,
                    payload.Title ?? title,
                    payload.SteamAppId,
                    cancellationToken);
            }
            else if (!isMatchRoute && method is "GET" or "POST")
            {
                var forceRefresh = method == "POST";
                var refreshOnOpen = !forceRefresh &&
                    bool.TryParse(request.QueryString["open"], out var parsedOpen) &&
                    parsedOpen;
                snapshot = await service.GetAsync(
                    appId,
                    title,
                    forceRefresh,
                    refreshOnOpen,
                    cancellationToken);
            }
            else
            {
                await WriteAsync(response, HttpStatusCode.MethodNotAllowed, new { message = "Method not allowed." }, cancellationToken);
                return true;
            }

            await WriteAsync(
                response,
                snapshot is null ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                snapshot is null
                    ? (object)new { message = "Metadata needs a non-Steam shortcut with a title." }
                    : snapshot,
                cancellationToken);
            return true;
        }
        catch (JsonException)
        {
            await WriteAsync(response, HttpStatusCode.BadRequest, new { message = "The request body is not valid JSON." }, cancellationToken);
            return true;
        }
        catch (HttpRequestException)
        {
            await WriteAsync(response, HttpStatusCode.BadGateway, new { message = "The metadata source could not be reached." }, cancellationToken);
            return true;
        }
    }

    // Steam gives every non-Steam shortcut an app ID with the top bit set.
    internal static bool TryParseShortcutAppId(string? value, out uint appId) =>
        uint.TryParse(value, out appId) && appId >= 0x80000000u;

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

    internal sealed record SetGameMetadataMatchRequest(int SteamAppId, string? Title);

    internal sealed record OpenMetadataLinkRequest(string? Value);

    internal sealed record SaveGameMetadataEditsRequest(GameMetadataEdits? Edits, string? Title);

    internal sealed record SetIgnPageRequest(string? Value, string? Title);
}
