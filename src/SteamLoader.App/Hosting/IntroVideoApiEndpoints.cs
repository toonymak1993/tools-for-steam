using System.Globalization;
using System.Net;
using System.Text.Json;
using SteamLoader.App.Infrastructure.Assets;
using SteamLoader.App.Infrastructure.Settings;

namespace SteamLoader.App.Hosting;

/// <summary>
/// <c>/api/intro/*</c> for the controller intro video (intro-video.js). The
/// video is shown once in Big Picture after installation and can be replayed
/// from TFS Settings. The MP4s are embedded assets; they are copied to
/// <c>data\intro</c> once so the player gets HTTP range requests (seeking).
/// </summary>
internal static class IntroVideoApiEndpoints
{
    public const string RoutePrefix = "/api/intro";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] Languages = ["de", "en"];
    private static readonly SemaphoreSlim ExtractGate = new(1, 1);

    public static bool Handles(string? path) =>
        path is not null &&
        (path.Equals(RoutePrefix, StringComparison.Ordinal) ||
         path.StartsWith(RoutePrefix + "/", StringComparison.Ordinal));

    /// <summary>Returns false when the request is not an intro route.</summary>
    public static async Task<bool> TryHandleAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        SteamLoaderSettingsService settings,
        CancellationToken cancellationToken)
    {
        var path = request.Url?.AbsolutePath;
        if (!Handles(path))
        {
            return false;
        }

        var isGet = request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase);
        var isPost = request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase);
        switch (path)
        {
            case "/api/intro/state" when isGet:
                await WriteAsync(response, HttpStatusCode.OK, new
                {
                    pending = settings.IsIntroVideoPending(),
                    languages = Languages,
                    defaultLanguage = ResolveWindowsLanguage()
                }, cancellationToken);
                return true;

            case "/api/intro/seen" when isPost:
                settings.MarkIntroVideoSeen();
                await WriteAsync(response, HttpStatusCode.OK, new { pending = false }, cancellationToken);
                return true;

            case "/api/intro/video" when isGet:
            {
                var language = NormalizeLanguage(request.QueryString["lang"]);
                var file = await EnsureVideoFileAsync(language, cancellationToken);
                if (file is null)
                {
                    await WriteAsync(response, HttpStatusCode.NotFound, new { message = "The intro video is not available." }, cancellationToken);
                    return true;
                }

                await ServeFileAsync(request, response, file, cancellationToken);
                return true;
            }
        }

        await WriteAsync(response, HttpStatusCode.NotFound, new { message = "Unknown intro route." }, cancellationToken);
        return true;
    }

    internal static string NormalizeLanguage(string? value)
    {
        var language = (value ?? string.Empty).Trim().ToLowerInvariant();
        return Languages.Contains(language) ? language : ResolveWindowsLanguage();
    }

    private static string ResolveWindowsLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase)
            ? "de"
            : "en";

    private static async Task<string?> EnsureVideoFileAsync(string language, CancellationToken cancellationToken)
    {
        var assetPath = $"Assets/Intro/tfs-intro-{language}.mp4";
        var directory = Path.Combine(AppContext.BaseDirectory, "data", "intro");
        var target = Path.Combine(directory, $"tfs-intro-{language}.mp4");
        await ExtractGate.WaitAsync(cancellationToken);
        try
        {
            using var source = EmbeddedAssetReader.TryOpenStream(assetPath);
            if (source is null)
            {
                return null;
            }

            // Re-copy after an update shipped a different video (length differs).
            if (File.Exists(target) && new FileInfo(target).Length == source.Length)
            {
                return target;
            }

            Directory.CreateDirectory(directory);
            var temporary = target + ".tmp";
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await source.CopyToAsync(output, cancellationToken);
            }

            File.Move(temporary, target, overwrite: true);
            return target;
        }
        finally
        {
            ExtractGate.Release();
        }
    }

    private static async Task ServeFileAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        string file,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, useAsync: true);
        var length = stream.Length;
        response.ContentType = "video/mp4";
        response.Headers["Accept-Ranges"] = "bytes";
        response.Headers["Cache-Control"] = "private, max-age=3600";

        long start = 0;
        var end = length - 1;
        if (OvertureApiEndpoints.TryParseRange(request.Headers["Range"], length, out var rangeStart, out var rangeEnd))
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

    private static async Task WriteAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        object payload,
        CancellationToken cancellationToken)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers["Cache-Control"] = "no-cache, no-store";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        response.ContentLength64 = bytes.Length;
        await using var output = response.OutputStream;
        await output.WriteAsync(bytes, cancellationToken);
    }
}
