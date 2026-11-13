using System.Net;
using System.Text.Json;
using SteamLoader.App.Infrastructure.PerfTab;

namespace SteamLoader.App.Hosting;

/// <summary><c>/api/perf-tab/*</c>: the TFS section in Steam's Performance tab.</summary>
internal static class PerfTabApiEndpoints
{
    private const string RoutePrefix = "/api/perf-tab/";
    private const string QuickSettingsPrefix = "/api/quick-settings/";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary><c>/api/quick-settings/*</c>: TFS rows in Steam's Quick Settings tab.</summary>
    public static async Task<bool> TryHandleQuickSettingsAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        QuickSettingsService? service,
        CancellationToken cancellationToken)
    {
        var path = request.Url?.AbsolutePath;
        if (path is null || !path.StartsWith(QuickSettingsPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (service is null)
        {
            await WriteAsync(response, HttpStatusCode.ServiceUnavailable, new { message = "Quick settings are not available." }, cancellationToken);
            return true;
        }

        var isPost = request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase);
        try
        {
            object result;
            switch (path[QuickSettingsPrefix.Length..])
            {
                case "state" when !isPost:
                    result = service.GetState();
                    break;
                case "output-volume" when isPost:
                    result = service.SetOutputVolume((await ReadAsync<IntValue>(request, cancellationToken))?.Value ?? 0);
                    break;
                case "input-volume" when isPost:
                    result = service.SetInputVolume((await ReadAsync<IntValue>(request, cancellationToken))?.Value ?? 0);
                    break;
                case "output-muted" when isPost:
                    result = service.SetOutputMuted((await ReadAsync<BoolValue>(request, cancellationToken))?.Value ?? false);
                    break;
                case "input-muted" when isPost:
                    result = service.SetInputMuted((await ReadAsync<BoolValue>(request, cancellationToken))?.Value ?? false);
                    break;
                case "output-device" when isPost:
                    result = service.SetOutputDevice((await ReadAsync<StringValue>(request, cancellationToken))?.Value ?? string.Empty);
                    break;
                case "input-device" when isPost:
                    result = service.SetInputDevice((await ReadAsync<StringValue>(request, cancellationToken))?.Value ?? string.Empty);
                    break;
                case "app-volume" when isPost:
                {
                    var body = await ReadAsync<AppIntValue>(request, cancellationToken);
                    result = service.SetAppVolume(body?.Id ?? string.Empty, body?.Value ?? 0);
                    break;
                }
                case "app-muted" when isPost:
                {
                    var body = await ReadAsync<AppBoolValue>(request, cancellationToken);
                    result = service.SetAppMuted(body?.Id ?? string.Empty, body?.Value ?? false);
                    break;
                }
                case "brightness" when isPost:
                    result = service.SetBrightness((await ReadAsync<IntValue>(request, cancellationToken))?.Value ?? 0);
                    break;
                case "resolution" when isPost:
                    result = service.SetResolution((await ReadAsync<StringValue>(request, cancellationToken))?.Value ?? string.Empty);
                    break;
                case "hdr" when isPost:
                    result = service.SetHdr((await ReadAsync<BoolValue>(request, cancellationToken))?.Value ?? false);
                    break;
                case "display-output" when isPost:
                    result = service.SetDisplayOutput((await ReadAsync<StringValue>(request, cancellationToken))?.Value ?? string.Empty);
                    break;
                default:
                    await WriteAsync(response, HttpStatusCode.NotFound, new { message = "Unknown quick settings route." }, cancellationToken);
                    return true;
            }

            await WriteAsync(response, HttpStatusCode.OK, result, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or JsonException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            await WriteAsync(response, HttpStatusCode.BadRequest, new { message = exception.Message }, cancellationToken);
        }

        return true;
    }

    public static async Task<bool> TryHandleAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        PerfTabService? service,
        CancellationToken cancellationToken)
    {
        var path = request.Url?.AbsolutePath;
        if (path is null || !path.StartsWith(RoutePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (service is null)
        {
            await WriteAsync(response, HttpStatusCode.ServiceUnavailable, new { message = "Performance controls are not available." }, cancellationToken);
            return true;
        }

        var isPost = request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase);
        try
        {
            object result;
            switch (path[RoutePrefix.Length..])
            {
                case "state" when !isPost:
                    result = service.GetState();
                    break;
                case "frame-limit" when isPost:
                    result = service.SetFrameLimit((await ReadAsync<IntValue>(request, cancellationToken))?.Value ?? 0);
                    break;
                case "refresh-rate" when isPost:
                    result = service.SetRefreshRate((await ReadAsync<IntValue>(request, cancellationToken))?.Value ?? 0);
                    break;
                case "power-mode" when isPost:
                    result = service.SetPowerMode((await ReadAsync<StringValue>(request, cancellationToken))?.Value ?? string.Empty);
                    break;
                case "cpu-boost" when isPost:
                    result = service.SetCpuBoost((await ReadAsync<BoolValue>(request, cancellationToken))?.Value ?? true);
                    break;
                case "tdp" when isPost:
                    result = service.SetTdp((await ReadAsync<IntValue>(request, cancellationToken))?.Value ?? 0);
                    break;
                case "rtss" when isPost:
                    result = service.SetRtssLevel((await ReadAsync<IntValue>(request, cancellationToken))?.Value ?? 0);
                    break;
                case "game-profile" when isPost:
                    result = service.SetGameProfile((await ReadAsync<BoolValue>(request, cancellationToken))?.Value ?? false);
                    break;
                case "auto-profiles" when isPost:
                    result = service.SetAutoProfiles((await ReadAsync<BoolValue>(request, cancellationToken))?.Value ?? true);
                    break;
                case "running-apps" when !isPost:
                    result = service.ListRunningApps();
                    break;
                case "add-profile" when isPost:
                    result = service.AddProfile((await ReadAsync<StringValue>(request, cancellationToken))?.Value ?? string.Empty);
                    break;
                case "add-app-profile" when isPost:
                {
                    var body = await ReadAsync<AppProfileValue>(request, cancellationToken);
                    result = service.AddProfileForApp(body?.AppId ?? 0, body?.Title);
                    break;
                }
                case "profile" when !isPost:
                    result = service.GetProfile(request.QueryString["key"] ?? string.Empty);
                    break;
                case "profile" when isPost:
                    result = service.SaveProfile(await ReadAsync<PerfTabProfileUpdate>(request, cancellationToken)
                        ?? throw new ArgumentException("The profile could not be read."));
                    break;
                case "delete-profile" when isPost:
                    result = service.DeleteProfile((await ReadAsync<StringValue>(request, cancellationToken))?.Value ?? string.Empty);
                    break;
                default:
                    await WriteAsync(response, HttpStatusCode.NotFound, new { message = "Unknown performance route." }, cancellationToken);
                    return true;
            }

            await WriteAsync(response, HttpStatusCode.OK, result, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or JsonException or System.ComponentModel.Win32Exception)
        {
            await WriteAsync(response, HttpStatusCode.BadRequest, new { message = exception.Message }, cancellationToken);
        }

        return true;
    }

    private static async Task<T?> ReadAsync<T>(HttpListenerRequest request, CancellationToken cancellationToken) =>
        await JsonSerializer.DeserializeAsync<T>(request.InputStream, JsonOptions, cancellationToken);

    private static async Task WriteAsync(HttpListenerResponse response, HttpStatusCode statusCode, object payload, CancellationToken cancellationToken)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        await using var output = response.OutputStream;
        await JsonSerializer.SerializeAsync(output, payload, payload.GetType(), JsonOptions, cancellationToken);
    }

    private sealed record IntValue(int Value);

    private sealed record StringValue(string? Value);

    private sealed record BoolValue(bool Value);

    private sealed record AppIntValue(string? Id, int Value);

    private sealed record AppBoolValue(string? Id, bool Value);

    private sealed record AppProfileValue(uint AppId, string? Title);
}
