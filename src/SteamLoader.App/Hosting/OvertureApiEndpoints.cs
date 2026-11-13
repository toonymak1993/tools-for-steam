using System.Globalization;
using System.Net;
using System.Text.Json;
using SteamLoader.App.Infrastructure.Overture;

namespace SteamLoader.App.Hosting;

/// <summary>
/// <c>/api/overture/*</c> for TFS Overture (game page theme music). The API
/// server dispatches here after its token and plugin checks.
/// </summary>
internal static class OvertureApiEndpoints
{
    public const string RoutePrefix = "/api/overture";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool Handles(string? path) =>
        path is not null &&
        (path.Equals(RoutePrefix, StringComparison.Ordinal) ||
         path.StartsWith(RoutePrefix + "/", StringComparison.Ordinal));

    /// <summary>Returns false when the request is not an Overture route.</summary>
    public static async Task<bool> TryHandleAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        OvertureService? service,
        CancellationToken cancellationToken)
    {
        var path = request.Url?.AbsolutePath;
        if (!Handles(path))
        {
            return false;
        }

        if (service is null)
        {
            await WriteAsync(response, HttpStatusCode.ServiceUnavailable, new { message = "TFS Overture is not available." }, cancellationToken);
            return true;
        }

        var isGet = request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase);
        var isPost = request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase);
        try
        {
            switch (path)
            {
                case "/api/overture/settings" when isGet:
                    await WriteAsync(response, HttpStatusCode.OK, service.GetSettings(), cancellationToken);
                    return true;

                case "/api/overture/settings" when isPost:
                {
                    var update = await ReadAsync<OvertureGlobalUpdate>(request, cancellationToken);
                    if (update is null)
                    {
                        await BadRequestAsync(response, "Settings are required.", cancellationToken);
                        return true;
                    }

                    await WriteAsync(response, HttpStatusCode.OK, service.SaveSettings(update), cancellationToken);
                    return true;
                }

                case "/api/overture/playlist" when isGet:
                {
                    var key = request.QueryString["key"];
                    if (!OvertureSettingsStore.IsValidKey(key))
                    {
                        await BadRequestAsync(response, "A valid playlist key is required.", cancellationToken);
                        return true;
                    }

                    await WriteAsync(response, HttpStatusCode.OK, service.GetPlaylist(key!, request.QueryString["title"]), cancellationToken);
                    return true;
                }

                case "/api/overture/playlist" or "/api/overture/playlist/reset" or "/api/overture/playlist/remove" when isPost:
                {
                    var payload = await ReadAsync<PlaylistRequest>(request, cancellationToken);
                    if (payload is null || !OvertureSettingsStore.IsValidKey(payload.Key))
                    {
                        await BadRequestAsync(response, "A valid playlist key is required.", cancellationToken);
                        return true;
                    }

                    var snapshot = path switch
                    {
                        "/api/overture/playlist/reset" => service.ResetPlaylist(payload.Key!, payload.Title),
                        "/api/overture/playlist/remove" => service.RemoveTrack(payload.Key!, payload.TrackId ?? string.Empty, payload.Title),
                        _ => service.SavePlaylist(
                            payload.Key!,
                            payload.Update ?? new OverturePlaylistUpdate(null, null, null, null, null, null),
                            payload.Title),
                    };
                    await WriteAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                    return true;
                }

                case "/api/overture/auto" when isPost:
                {
                    var payload = await ReadAsync<PlaylistRequest>(request, cancellationToken);
                    if (payload is null ||
                        payload.Key == OvertureConfiguration.AmbientKey ||
                        !OvertureSettingsStore.IsValidKey(payload.Key))
                    {
                        await BadRequestAsync(response, "A valid game is required.", cancellationToken);
                        return true;
                    }

                    var appId = uint.Parse(payload.Key!, NumberStyles.None, CultureInfo.InvariantCulture);
                    await WriteAsync(response, HttpStatusCode.OK, await service.AutoFindAsync(appId, payload.Title, cancellationToken), cancellationToken);
                    return true;
                }

                case "/api/overture/youtube" when isGet:
                    await WriteAsync(response, HttpStatusCode.OK, service.Youtube.GetStatus(), cancellationToken);
                    return true;

                case "/api/overture/youtube/install" when isPost:
                    await WriteAsync(response, HttpStatusCode.OK, service.Youtube.StartInstall(), cancellationToken);
                    return true;

                case "/api/overture/prefetch" when isGet:
                    await WriteAsync(response, HttpStatusCode.OK, service.GetPrefetchStatus(), cancellationToken);
                    return true;

                case "/api/overture/prefetch" when isPost:
                {
                    var payload = await ReadAsync<PrefetchRequest>(request, cancellationToken);
                    var games = (payload?.Games ?? [])
                        .Where(game => game is not null && game.AppId > 0)
                        .Take(OvertureService.MaxPrefetchGames)
                        .Select(game => (game.AppId, game.Title ?? string.Empty))
                        .ToList();
                    await WriteAsync(response, HttpStatusCode.OK, service.StartPrefetch(games), cancellationToken);
                    return true;
                }

                case "/api/overture/youtube/search" when isGet:
                {
                    var items = await service.SearchYoutubeAsync(request.QueryString["term"] ?? string.Empty, cancellationToken);
                    await WriteAsync(response, HttpStatusCode.OK, new { items }, cancellationToken);
                    return true;
                }

                case "/api/overture/youtube/add" when isPost:
                {
                    var payload = await ReadAsync<AddYoutubeRequest>(request, cancellationToken);
                    if (payload is null ||
                        !OvertureSettingsStore.IsValidKey(payload.Key) ||
                        !OvertureSettingsStore.IsValidVideoId(payload.VideoId))
                    {
                        await BadRequestAsync(response, "A playlist and a YouTube video are required.", cancellationToken);
                        return true;
                    }

                    var video = new OvertureSearchResult(
                        payload.VideoId!,
                        string.IsNullOrWhiteSpace(payload.VideoTitle) ? payload.VideoId! : payload.VideoTitle!,
                        payload.Channel,
                        payload.DurationSeconds is > 0 and < 86400 ? payload.DurationSeconds : null);
                    var snapshot = await service.AddYoutubeAsync(payload.Key!, video, payload.Title, cancellationToken);
                    await WriteAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                    return true;
                }

                case "/api/overture/local" when isGet:
                {
                    var items = service.ListLocalFiles(request.QueryString["term"], 80);
                    await WriteAsync(response, HttpStatusCode.OK, new { items }, cancellationToken);
                    return true;
                }

                case "/api/overture/local/add" when isPost:
                {
                    var payload = await ReadAsync<PlaylistRequest>(request, cancellationToken);
                    if (payload is null ||
                        !OvertureSettingsStore.IsValidKey(payload.Key) ||
                        string.IsNullOrWhiteSpace(payload.Path) ||
                        !Path.IsPathFullyQualified(payload.Path))
                    {
                        await BadRequestAsync(response, "A playlist and a music file are required.", cancellationToken);
                        return true;
                    }

                    await WriteAsync(response, HttpStatusCode.OK, service.AddLocal(payload.Key!, payload.Path, payload.Title), cancellationToken);
                    return true;
                }

                case "/api/overture/storage" when isGet:
                    await WriteAsync(response, HttpStatusCode.OK, service.GetStorage(), cancellationToken);
                    return true;

                case "/api/overture/storage/clean" when isPost:
                    await WriteAsync(response, HttpStatusCode.OK, service.DeleteUnusedDownloads(), cancellationToken);
                    return true;

                case "/api/overture/audio" when isGet:
                {
                    var file = service.ResolveAudioFile(request.QueryString["track"]);
                    if (file is null)
                    {
                        await WriteAsync(response, HttpStatusCode.NotFound, new { message = "Unknown or missing track." }, cancellationToken);
                        return true;
                    }

                    await ServeFileAsync(request, response, file, cancellationToken);
                    return true;
                }
            }

            await WriteAsync(response, HttpStatusCode.NotFound, new { message = "Unknown TFS Overture route." }, cancellationToken);
            return true;
        }
        catch (JsonException)
        {
            await BadRequestAsync(response, "The request body is not valid JSON.", cancellationToken);
            return true;
        }
        catch (ArgumentException exception)
        {
            await BadRequestAsync(response, exception.Message, cancellationToken);
            return true;
        }
        catch (InvalidOperationException exception)
        {
            await WriteAsync(response, HttpStatusCode.Conflict, new { message = exception.Message }, cancellationToken);
            return true;
        }
        catch (TimeoutException exception)
        {
            await WriteAsync(response, HttpStatusCode.GatewayTimeout, new { message = exception.Message }, cancellationToken);
            return true;
        }
    }

    /// <summary>Streams an audio file with HTTP range support (needed for seeking).</summary>
    private static async Task ServeFileAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        string file,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, useAsync: true);
        var length = stream.Length;
        response.ContentType = ContentTypeFor(file);
        response.Headers["Accept-Ranges"] = "bytes";
        response.Headers["Cache-Control"] = "private, max-age=3600";

        long start = 0;
        var end = length - 1;
        if (TryParseRange(request.Headers["Range"], length, out var rangeStart, out var rangeEnd))
        {
            start = rangeStart;
            end = rangeEnd;
            response.StatusCode = (int)HttpStatusCode.PartialContent;
            response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
        }
        else if (request.Headers["Range"] is { Length: > 0 })
        {
            response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
            response.Headers["Content-Range"] = $"bytes */{length}";
            response.Close();
            return;
        }
        else
        {
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        var count = Math.Max(0, end - start + 1);
        response.ContentLength64 = count;
        stream.Seek(start, SeekOrigin.Begin);
        try
        {
            await using var output = response.OutputStream;
            var buffer = new byte[65536];
            var remaining = count;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read <= 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }
        catch (HttpListenerException)
        {
            // The player closed the connection (seek, page left); nothing to do.
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Parses a single "bytes=a-b", "bytes=a-" or "bytes=-n" range.</summary>
    internal static bool TryParseRange(string? header, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;
        if (string.IsNullOrWhiteSpace(header) || length <= 0 ||
            !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(','))
        {
            return false;
        }

        var parts = header[6..].Trim().Split('-', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        var hasStart = long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first);
        var hasEnd = long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var last);
        if (hasStart)
        {
            if (first >= length) return false;
            start = first;
            end = hasEnd ? Math.Min(last, length - 1) : length - 1;
            return end >= start;
        }

        if (hasEnd && last > 0)
        {
            start = Math.Max(0, length - last);
            end = length - 1;
            return true;
        }

        return false;
    }

    internal static string ContentTypeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" or ".aac" => "audio/mp4",
        ".ogg" or ".oga" => "audio/ogg",
        ".opus" => "audio/ogg; codecs=opus",
        ".wav" => "audio/wav",
        ".flac" => "audio/flac",
        ".webm" => "audio/webm",
        _ => "application/octet-stream",
    };

    private static async Task<T?> ReadAsync<T>(HttpListenerRequest request, CancellationToken cancellationToken) =>
        await JsonSerializer.DeserializeAsync<T>(request.InputStream, JsonOptions, cancellationToken);

    private static Task BadRequestAsync(HttpListenerResponse response, string message, CancellationToken cancellationToken) =>
        WriteAsync(response, HttpStatusCode.BadRequest, new { message }, cancellationToken);

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

    internal sealed record PlaylistRequest(
        string? Key,
        string? Title,
        OverturePlaylistUpdate? Update,
        string? TrackId,
        string? Path);

    internal sealed record PrefetchGame(uint AppId, string? Title);

    internal sealed record PrefetchRequest(List<PrefetchGame>? Games);

    internal sealed record AddYoutubeRequest(
        string? Key,
        string? Title,
        string? VideoId,
        string? VideoTitle,
        string? Channel,
        double? DurationSeconds);
}
