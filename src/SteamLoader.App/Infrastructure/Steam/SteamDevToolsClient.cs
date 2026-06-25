using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace SteamLoader.App.Infrastructure.Steam;

public sealed class SteamDevToolsClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly Uri _debugEndpoint;
    private int _nextCommandId = 1;

    public SteamDevToolsClient(HttpClient httpClient, Uri debugEndpoint)
    {
        _httpClient = httpClient;
        _debugEndpoint = debugEndpoint;
    }

    public async Task<SteamDevToolsTarget?> GetSharedJsContextTargetAsync(CancellationToken cancellationToken)
    {
        var targets = await GetTargetsAsync(cancellationToken);
        return targets.FirstOrDefault(target =>
            string.Equals(target.Title, "SharedJSContext", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<SteamDevToolsTarget?> GetQuickAccessTargetAsync(CancellationToken cancellationToken)
    {
        var targets = await GetTargetsAsync(cancellationToken);
        return targets.FirstOrDefault(target => IsQuickAccessTarget(target));
    }

    public async Task<SteamDevToolsTarget?> GetBigPictureTargetAsync(CancellationToken cancellationToken)
    {
        var targets = await GetTargetsAsync(cancellationToken);
        return targets.FirstOrDefault(target => IsBigPictureMainTarget(target));
    }

    public async Task<bool> HasBigPictureSurfaceAsync(CancellationToken cancellationToken)
    {
        var targets = await GetTargetsAsync(cancellationToken);
        return targets.Any(target => IsBigPictureSurfaceTarget(target));
    }

    public async Task<IReadOnlyList<SteamDevToolsTarget>> GetThemeSurfaceTargetsAsync(CancellationToken cancellationToken)
    {
        var targets = await GetTargetsAsync(cancellationToken);
        return targets
            .Where(target =>
                string.Equals(target.Type, "page", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(target.Title, "SharedJSContext", StringComparison.OrdinalIgnoreCase) ||
                 IsBigPictureMainTarget(target) ||
                 IsSteamMenuSurfaceTarget(target) ||
                 target.Url.Contains("steamloopback.host", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public async Task<IReadOnlyList<SteamDevToolsTarget>> GetTargetsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var targetsUri = new Uri(_debugEndpoint, "/json/list");
            await using var stream = await _httpClient.GetStreamAsync(targetsUri, cancellationToken);
            var targets = await JsonSerializer.DeserializeAsync<List<SteamDevToolsTarget>>(stream, JsonOptions, cancellationToken);
            return targets ?? [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private static bool IsQuickAccessTarget(SteamDevToolsTarget target)
    {
        return target.Title.StartsWith("QuickAccess", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBigPictureMainTarget(SteamDevToolsTarget target)
    {
        return target.Title.Contains("Big-Picture", StringComparison.OrdinalIgnoreCase) ||
            target.Url.Contains("steamloopback.host/index.html", StringComparison.OrdinalIgnoreCase) ||
            target.Url.Contains("browserType=3", StringComparison.OrdinalIgnoreCase) ||
            target.Url.Contains("Valve%20Steam%20Gamepad", StringComparison.OrdinalIgnoreCase) ||
            target.Url.Contains("Valve Steam Gamepad", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBigPictureSurfaceTarget(SteamDevToolsTarget target)
    {
        return IsBigPictureMainTarget(target) ||
            IsQuickAccessTarget(target) ||
            target.Title.StartsWith("MainMenu", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSteamMenuSurfaceTarget(SteamDevToolsTarget target)
    {
        return target.Title.Equals("Menu", StringComparison.OrdinalIgnoreCase) ||
            target.Title.StartsWith("MainMenu", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<SteamDevToolsEvaluationResult> EvaluateAsync(
        string webSocketDebuggerUrl,
        string expression,
        CancellationToken cancellationToken)
    {
        using var webSocket = new ClientWebSocket();
        await webSocket.ConnectAsync(new Uri(webSocketDebuggerUrl), cancellationToken);

        var commandId = Interlocked.Increment(ref _nextCommandId);

        var payload = JsonSerializer.Serialize(
            new DevToolsCommand(
                commandId,
                "Runtime.evaluate",
                new DevToolsCommandParameters(expression, true, true)),
            JsonOptions);

        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        await webSocket.SendAsync(
            payloadBytes,
            WebSocketMessageType.Text,
            true,
            cancellationToken);

        DevToolsResponse? response = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var responseText = await ReceiveMessageAsync(webSocket, cancellationToken);
            response = TryDeserializeMatchingResponse(responseText, commandId);
            if (response is not null)
            {
                break;
            }
        }

        if (response is null)
        {
            return new SteamDevToolsEvaluationResult(false, null, "Steam DevTools did not return a matching evaluation response.");
        }

        if (response?.Error is not null)
        {
            return new SteamDevToolsEvaluationResult(false, null, response.Error.Message);
        }

        if (response?.Result?.ExceptionDetails is not null)
        {
            return new SteamDevToolsEvaluationResult(
                false,
                null,
                response.Result.ExceptionDetails.Text ?? "JavaScript evaluation failed.");
        }

        return new SteamDevToolsEvaluationResult(
            true,
            response?.Result?.Result?.Value,
            null);
    }

    private static async Task<string> ReceiveMessageAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var memory = new MemoryStream();

        while (true)
        {
            var result = await webSocket.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            memory.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static DevToolsResponse? TryDeserializeMatchingResponse(string responseText, int expectedCommandId)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("id", out var idElement) ||
                !idElement.TryGetInt32(out var actualCommandId) ||
                actualCommandId != expectedCommandId)
            {
                return null;
            }

            return JsonSerializer.Deserialize<DevToolsResponse>(root.GetRawText(), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private sealed record DevToolsCommand(int Id, string Method, DevToolsCommandParameters Params);

    private sealed record DevToolsCommandParameters(
        string Expression,
        bool ReturnByValue,
        bool AwaitPromise);

    private sealed record DevToolsResponse(
        int Id,
        DevToolsResponsePayload? Result,
        DevToolsError? Error);

    private sealed record DevToolsResponsePayload(
        DevToolsRemoteObject? Result,
        DevToolsExceptionDetails? ExceptionDetails);

    private sealed record DevToolsRemoteObject(
        string? Type,
        object? Value,
        string? Description);

    private sealed record DevToolsExceptionDetails(
        string? Text);

    private sealed record DevToolsError(
        int Code,
        string Message);
}
