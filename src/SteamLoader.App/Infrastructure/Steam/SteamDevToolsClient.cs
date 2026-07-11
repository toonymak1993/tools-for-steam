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

    /// <summary>
    /// Delivers a Ctrl+&lt;digit&gt; shortcut straight into Steam's Big Picture UI
    /// through the CEF debugger (Input.dispatchKeyEvent). Because the key event is
    /// injected into Steam's own renderer, it triggers the built-in Gamepad UI
    /// shortcuts (Ctrl+1 = STEAM menu, Ctrl+2 = Quick Access) reliably, regardless
    /// of which window currently holds OS keyboard focus. Returns false if Big
    /// Picture is not running or the event could not be delivered.
    /// </summary>
    public async Task<bool> SendControlDigitShortcutAsync(int digit, CancellationToken cancellationToken)
    {
        if (digit is < 0 or > 9)
        {
            return false;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
        var operationToken = timeoutCts.Token;

        try
        {
            var target = await GetBigPictureTargetAsync(operationToken)
                ?? await GetSharedJsContextTargetAsync(operationToken);
            if (target is null || string.IsNullOrEmpty(target.WebSocketDebuggerUrl))
            {
                return false;
            }

            using var webSocket = new ClientWebSocket();
            await webSocket.ConnectAsync(new Uri(target.WebSocketDebuggerUrl), operationToken);

            var key = digit.ToString();
            var code = $"Digit{digit}";
            var virtualKeyCode = 0x30 + digit; // VK_0 .. VK_9
            const int ctrlModifier = 2;        // CDP modifiers bitmask: Alt=1, Ctrl=2, Meta=4, Shift=8

            // Ctrl down, <digit> down (Ctrl held), <digit> up, Ctrl up.
            await SendKeyEventAsync(webSocket, "rawKeyDown", "Control", "ControlLeft", 0x11, ctrlModifier, operationToken);
            await SendKeyEventAsync(webSocket, "rawKeyDown", key, code, virtualKeyCode, ctrlModifier, operationToken);
            await SendKeyEventAsync(webSocket, "keyUp", key, code, virtualKeyCode, ctrlModifier, operationToken);
            var lastCommandId = await SendKeyEventAsync(webSocket, "keyUp", "Control", "ControlLeft", 0x11, 0, operationToken);

            // Wait for Steam to acknowledge the final event so the socket isn't
            // closed before the whole sequence is processed.
            await WaitForAckAsync(webSocket, lastCommandId, operationToken);

            try
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, operationToken);
            }
            catch
            {
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public Task<bool> TryOpenSteamMenuAsync(CancellationToken cancellationToken)
    {
        return TryInvokeSteamSurfaceActionAsync(
            BuildMenuActionExpression(
                openMethodNames:
                [
                    "OpenMainMenu",
                    "ShowMainMenu",
                    "OpenSteamMenu",
                    "ShowSteamMenu",
                    "OpenMenu",
                    "ShowMenu",
                    "OpenSideMenus",
                    "OpenSideMenu",
                    "ShowSideMenus",
                    "ShowSideMenu"
                ],
                setVisibleMethodNames:
                [
                    "SetMainMenuVisible",
                    "SetSteamMenuVisible",
                    "SetMenuVisible",
                    "SetMainMenuOpen",
                    "SetMenuOpen",
                    "SetSideMenuVisible",
                    "SetSideMenuOpen"
                ],
                openArgs:
                [
                    [],
                    ["menu"],
                    ["Menu"],
                    ["mainmenu"],
                    ["MainMenu"],
                    ["main-menu"],
                    ["mainMenu"],
                    ["steam"],
                    ["Steam"],
                    ["steammenu"],
                    ["SteamMenu"],
                    ["left"],
                    ["Left"]
                ],
                setVisibleArgs:
                [
                    [true],
                    ["menu", true],
                    ["Menu", true],
                    ["mainmenu", true],
                    ["MainMenu", true],
                    ["main-menu", true],
                    ["mainMenu", true],
                    ["steam", true],
                    ["Steam", true],
                    ["steammenu", true],
                    ["SteamMenu", true],
                    ["left", true],
                    ["Left", true]
                ]),
            cancellationToken);
    }

    public Task<bool> TryOpenQuickAccessMenuAsync(CancellationToken cancellationToken)
    {
        return TryInvokeSteamSurfaceActionAsync(
            BuildMenuActionExpression(
                openMethodNames:
                [
                    "OpenQuickAccessMenu",
                    "ShowQuickAccessMenu",
                    "OpenSideMenus",
                    "OpenSideMenu",
                    "ShowSideMenus",
                    "ShowSideMenu"
                ],
                setVisibleMethodNames:
                [
                    "SetQuickAccessMenuVisible",
                    "SetQuickAccessVisible",
                    "SetSideMenuVisible",
                    "SetSideMenuOpen"
                ],
                openArgs:
                [
                    [],
                    ["quickaccess"],
                    ["QuickAccess"],
                    ["quick-access"],
                    ["quickAccess"],
                    ["right"],
                    ["Right"]
                ],
                setVisibleArgs:
                [
                    [true],
                    ["quickaccess", true],
                    ["QuickAccess", true],
                    ["quick-access", true],
                    ["quickAccess", true],
                    ["right", true],
                    ["Right", true]
                ]),
            cancellationToken);
    }

    private async Task<int> SendKeyEventAsync(
        ClientWebSocket webSocket,
        string type,
        string key,
        string code,
        int windowsVirtualKeyCode,
        int modifiers,
        CancellationToken cancellationToken)
    {
        var commandId = Interlocked.Increment(ref _nextCommandId);
        var command = new Dictionary<string, object?>
        {
            ["id"] = commandId,
            ["method"] = "Input.dispatchKeyEvent",
            ["params"] = new Dictionary<string, object?>
            {
                ["type"] = type,
                ["modifiers"] = modifiers,
                ["windowsVirtualKeyCode"] = windowsVirtualKeyCode,
                ["nativeVirtualKeyCode"] = windowsVirtualKeyCode,
                ["key"] = key,
                ["code"] = code,
            },
        };

        var payloadBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, JsonOptions));
        await webSocket.SendAsync(payloadBytes, WebSocketMessageType.Text, true, cancellationToken);
        return commandId;
    }

    private static async Task WaitForAckAsync(
        ClientWebSocket webSocket,
        int expectedCommandId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var responseText = await ReceiveMessageAsync(webSocket, cancellationToken);
            if (string.IsNullOrWhiteSpace(responseText))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(responseText);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("id", out var idElement) &&
                    idElement.TryGetInt32(out var actualCommandId) &&
                    actualCommandId == expectedCommandId)
                {
                    return;
                }
            }
            catch
            {
                return;
            }
        }
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

    private async Task<bool> TryInvokeSteamSurfaceActionAsync(
        string expression,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
        var operationToken = timeoutCts.Token;

        try
        {
            var targets = await GetTargetsAsync(operationToken);
            var candidateTargets = targets
                .Where(IsSteamSurfaceActionTarget)
                .Where(target => !string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
                .OrderBy(GetSteamSurfaceActionPriority)
                .GroupBy(target => target.WebSocketDebuggerUrl, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

            foreach (var target in candidateTargets)
            {
                var result = await EvaluateAsync(target.WebSocketDebuggerUrl, expression, operationToken);
                if (result.Success && TryReadBoolean(result.Value, out var handled) && handled)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }

        return false;
    }

    private static bool IsSteamSurfaceActionTarget(SteamDevToolsTarget target)
    {
        return string.Equals(target.Type, "page", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(target.Title, "SharedJSContext", StringComparison.OrdinalIgnoreCase) ||
             IsBigPictureSurfaceTarget(target) ||
             IsSteamMenuSurfaceTarget(target));
    }

    private static int GetSteamSurfaceActionPriority(SteamDevToolsTarget target)
    {
        if (string.Equals(target.Title, "SharedJSContext", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (IsBigPictureMainTarget(target))
        {
            return 1;
        }

        if (target.Title.StartsWith("MainMenu", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (target.Title.Equals("Menu", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (IsQuickAccessTarget(target))
        {
            return 4;
        }

        return 5;
    }

    private static bool TryReadBoolean(object? value, out bool boolean)
    {
        switch (value)
        {
            case bool typedBoolean:
                boolean = typedBoolean;
                return true;
            case JsonElement { ValueKind: JsonValueKind.True }:
                boolean = true;
                return true;
            case JsonElement { ValueKind: JsonValueKind.False }:
                boolean = false;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } element
                when bool.TryParse(element.GetString(), out var parsedBoolean):
                boolean = parsedBoolean;
                return true;
            case string text when bool.TryParse(text, out var parsedBoolean):
                boolean = parsedBoolean;
                return true;
            default:
                boolean = false;
                return false;
        }
    }

    private static string BuildMenuActionExpression(
        IReadOnlyCollection<string> openMethodNames,
        IReadOnlyCollection<string> setVisibleMethodNames,
        IReadOnlyCollection<object?[]> openArgs,
        IReadOnlyCollection<object?[]> setVisibleArgs)
    {
        var openMethodsJson = JsonSerializer.Serialize(openMethodNames, JsonOptions);
        var setVisibleMethodsJson = JsonSerializer.Serialize(setVisibleMethodNames, JsonOptions);
        var openArgsJson = JsonSerializer.Serialize(openArgs, JsonOptions);
        var setVisibleArgsJson = JsonSerializer.Serialize(setVisibleArgs, JsonOptions);

        return $$"""
(() => {
  const tryInvoke = (target, methodNames, argsList) => {
    if (!target || (typeof target !== "object" && typeof target !== "function")) {
      return false;
    }

    for (const name of methodNames) {
      const method = target?.[name];
      if (typeof method !== "function") {
        continue;
      }

      for (const args of argsList) {
        try {
          method.apply(target, Array.isArray(args) ? args : []);
          return true;
        } catch {
        }
      }
    }

    return false;
  };

  const openMethodNames = {{openMethodsJson}};
  const setVisibleMethodNames = {{setVisibleMethodsJson}};
  const openArgs = {{openArgsJson}};
  const setVisibleArgs = {{setVisibleArgsJson}};
  const candidates = [
    window.GamepadUI,
    window.GamepadUI?.Router,
    window.GamepadUI?.NavigationManager,
    window.SteamUIStore,
    window.SteamUIStore?.MenuStore,
    window.SteamUIStore?.SideMenuStore,
    window.SteamClient?.UI,
    window.SteamClient?.Overlay,
    window.SteamClient?.Input,
    window.SteamClient?.System,
    window.SteamClient,
  ];

  for (const candidate of candidates) {
    if (tryInvoke(candidate, openMethodNames, openArgs) || tryInvoke(candidate, setVisibleMethodNames, setVisibleArgs)) {
      return true;
    }
  }

  return false;
})()
""";
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
