using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SteamLoader.App.Infrastructure.Steam;

/// <summary>
/// One long-lived Chrome DevTools Protocol connection to a single Steam CEF
/// target. Commands are multiplexed over the socket and page events are
/// delivered as they happen, so the injector can react to reloads instead of
/// polling every target with a fresh WebSocket.
/// </summary>
public sealed class SteamCdpSession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveLoop;
    private int _nextId;

    private SteamCdpSession(SteamDevToolsTarget target)
    {
        Target = target;
    }

    public SteamDevToolsTarget Target { get; }

    /// <summary>Completes when the socket closes for any reason (target gone, Steam restart, dispose).</summary>
    public Task Closed => _closed.Task;

    public bool IsOpen => _socket.State == WebSocketState.Open && !_closed.Task.IsCompleted;

    /// <summary>Raised on the receive loop for every CDP event (method + params).</summary>
    public event Action<string, JsonElement>? EventReceived;

    public static async Task<SteamCdpSession> ConnectAsync(
        SteamDevToolsTarget target,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
        {
            throw new InvalidOperationException($"Steam target '{target.Title}' has no debugger URL.");
        }

        var session = new SteamCdpSession(target);
        try
        {
            session._socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await session._socket.ConnectAsync(new Uri(target.WebSocketDebuggerUrl), connectTimeout.Token);
            session._receiveLoop = Task.Run(session.ReceiveLoopAsync);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public async Task<JsonElement> SendAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        if (!IsOpen)
        {
            throw new WebSocketException("The Steam DevTools session is closed.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["method"] = method,
                    ["params"] = parameters ?? new Dictionary<string, object?>(),
                },
                JsonOptions);

            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _sendGate.Release();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
            return await completion.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new WebSocketException("The Steam DevTools session closed while waiting for a reply.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Runtime.evaluate over the persistent socket (returnByValue, optional promise await).</summary>
    public async Task<SteamDevToolsEvaluationResult> EvaluateAsync(
        string expression,
        bool awaitPromise,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        JsonElement reply;
        try
        {
            reply = await SendAsync(
                "Runtime.evaluate",
                new Dictionary<string, object?>
                {
                    ["expression"] = expression,
                    ["returnByValue"] = true,
                    ["awaitPromise"] = awaitPromise,
                },
                cancellationToken,
                timeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SteamDevToolsEvaluationResult(false, null, exception.Message);
        }

        return ReadEvaluationReply(reply);
    }

    internal static SteamDevToolsEvaluationResult ReadEvaluationReply(JsonElement reply)
    {
        if (reply.ValueKind != JsonValueKind.Object)
        {
            return new SteamDevToolsEvaluationResult(false, null, "Steam DevTools returned no evaluation result.");
        }

        if (reply.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : error.GetRawText();
            return new SteamDevToolsEvaluationResult(false, null, message ?? "Steam DevTools reported an error.");
        }

        if (!reply.TryGetProperty("result", out var result))
        {
            return new SteamDevToolsEvaluationResult(false, null, "Steam DevTools returned no evaluation result.");
        }

        if (result.TryGetProperty("exceptionDetails", out var exceptionDetails))
        {
            var text = exceptionDetails.TryGetProperty("exception", out var exception) &&
                exception.TryGetProperty("description", out var description)
                    ? description.GetString()
                    : exceptionDetails.TryGetProperty("text", out var textElement)
                        ? textElement.GetString()
                        : null;
            return new SteamDevToolsEvaluationResult(false, null, text ?? "JavaScript evaluation failed.");
        }

        if (result.TryGetProperty("result", out var remoteObject) &&
            remoteObject.TryGetProperty("value", out var value))
        {
            return new SteamDevToolsEvaluationResult(true, value.Clone(), null);
        }

        return new SteamDevToolsEvaluationResult(true, null, null);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var message = new MemoryStream();
            while (!_lifetime.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, _lifetime.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                Dispatch(message.GetBuffer().AsSpan(0, (int)message.Length));
            }
        }
        catch
        {
            // Socket faults end the session; Closed tells the owner to reattach.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new WebSocketException("The Steam DevTools session closed."));
            }

            _closed.TrySetResult();
        }
    }

    private void Dispatch(ReadOnlySpan<byte> utf8)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8.ToArray());
        }
        catch
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
            {
                if (_pending.TryGetValue(id, out var completion))
                {
                    completion.TrySetResult(root.Clone());
                }

                return;
            }

            if (root.TryGetProperty("method", out var methodElement) &&
                methodElement.GetString() is { Length: > 0 } method)
            {
                var parameters = root.TryGetProperty("params", out var paramsElement)
                    ? paramsElement.Clone()
                    : default;
                try
                {
                    EventReceived?.Invoke(method, parameters);
                }
                catch
                {
                    // A faulty listener must not kill the receive loop.
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closeTimeout.Token);
            }
        }
        catch
        {
        }

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }

        _closed.TrySetResult();
        _socket.Dispose();
        _sendGate.Dispose();
        _lifetime.Dispose();
    }
}
