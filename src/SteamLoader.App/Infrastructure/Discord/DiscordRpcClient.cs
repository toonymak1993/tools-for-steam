using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamLoader.App.Infrastructure.Discord;

internal interface IDiscordRpcClient : IAsyncDisposable
{
    Task<DiscordRpcAuthentication> AuthorizeAsync(string applicationId, CancellationToken cancellationToken);

    Task<DiscordRpcAuthentication> AuthenticateAsync(
        string applicationId,
        string accessToken,
        CancellationToken cancellationToken);

    Task<DiscordRpcToken> RefreshTokenAsync(
        string applicationId,
        string refreshToken,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DiscordRpcGuild>> GetGuildsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DiscordRpcChannel>> GetGuildChannelsAsync(
        string guildId,
        CancellationToken cancellationToken);

    Task<DiscordRpcChannelDetails> GetChannelAsync(
        string channelId,
        CancellationToken cancellationToken);

    Task SelectVoiceChannelAsync(string channelId, CancellationToken cancellationToken);

    Task<string> GetSelectedVoiceChannelIdAsync(CancellationToken cancellationToken);

    Task DisconnectAsync();
}

internal sealed record DiscordRpcToken(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAtUtc);

internal sealed record DiscordRpcAuthentication(
    DiscordRpcToken Token,
    DiscordRpcUser User);

internal sealed record DiscordRpcUser(
    string Id,
    string Username,
    string DisplayName,
    string AvatarHash);

internal sealed record DiscordRpcGuild(
    string Id,
    string Name,
    string IconUrl);

internal sealed record DiscordRpcChannel(
    string Id,
    string Name,
    int Type,
    int Position);

internal sealed record DiscordRpcVoiceState(
    DiscordRpcUser User,
    string Nickname,
    bool Muted,
    bool Deafened);

internal sealed record DiscordRpcChannelDetails(
    string Id,
    string Name,
    int Type,
    IReadOnlyList<DiscordRpcVoiceState> VoiceStates);

internal class DiscordRpcException : InvalidOperationException
{
    public DiscordRpcException(string message, int? code = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public int? Code { get; }
}

internal sealed class DiscordNotRunningException : DiscordRpcException
{
    public DiscordNotRunningException()
        : base("Discord Desktop is not running. Start Discord, sign in, and try again.")
    {
    }
}

internal sealed class DiscordRpcClient : IDiscordRpcClient
{
    private const int RpcVersion = 1;
    private const int MaxFrameBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private string _applicationId = string.Empty;

    public DiscordRpcClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<DiscordRpcAuthentication> AuthorizeAsync(
        string applicationId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ConnectCoreAsync(applicationId, cancellationToken);
            using var authorizeResult = await SendCommandCoreAsync(
                "AUTHORIZE",
                new
                {
                    client_id = applicationId,
                    scopes = new[] { "rpc", "identify" }
                },
                TimeSpan.FromMinutes(3),
                cancellationToken);
            var code = GetRequiredString(authorizeResult.RootElement, "code", "Discord did not return an authorization code.");
            var token = await ExchangeAuthorizationCodeAsync(applicationId, code, cancellationToken);
            var user = await AuthenticateCoreAsync(token.AccessToken, cancellationToken);
            return new DiscordRpcAuthentication(token, user);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DiscordRpcAuthentication> AuthenticateAsync(
        string applicationId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ConnectCoreAsync(applicationId, cancellationToken);
            var user = await AuthenticateCoreAsync(accessToken, cancellationToken);
            return new DiscordRpcAuthentication(
                new DiscordRpcToken(accessToken, string.Empty, DateTimeOffset.MaxValue),
                user);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DiscordRpcToken> RefreshTokenAsync(
        string applicationId,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = applicationId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });
        return await RequestTokenAsync(content, cancellationToken);
    }

    public async Task<IReadOnlyList<DiscordRpcGuild>> GetGuildsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var result = await SendCommandCoreAsync(
                "GET_GUILDS",
                new { },
                TimeSpan.FromSeconds(20),
                cancellationToken);
            if (!result.RootElement.TryGetProperty("guilds", out var guildsElement) ||
                guildsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return guildsElement.EnumerateArray()
                .Select(ParseGuild)
                .Where(guild => !string.IsNullOrWhiteSpace(guild.Id))
                .OrderBy(guild => guild.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<DiscordRpcChannel>> GetGuildChannelsAsync(
        string guildId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var result = await SendCommandCoreAsync(
                "GET_CHANNELS",
                new { guild_id = guildId },
                TimeSpan.FromSeconds(20),
                cancellationToken);
            if (!result.RootElement.TryGetProperty("channels", out var channelsElement) ||
                channelsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return channelsElement.EnumerateArray()
                .Select(ParseChannel)
                .Where(channel => !string.IsNullOrWhiteSpace(channel.Id))
                .OrderBy(channel => channel.Position)
                .ThenBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DiscordRpcChannelDetails> GetChannelAsync(
        string channelId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var result = await SendCommandCoreAsync(
                "GET_CHANNEL",
                new { channel_id = channelId },
                TimeSpan.FromSeconds(20),
                cancellationToken);
            var root = result.RootElement;
            var voiceStates = root.TryGetProperty("voice_states", out var statesElement) &&
                              statesElement.ValueKind == JsonValueKind.Array
                ? statesElement.EnumerateArray().Select(ParseVoiceState).Where(state => state is not null).Cast<DiscordRpcVoiceState>().ToArray()
                : [];
            return new DiscordRpcChannelDetails(
                GetString(root, "id"),
                GetString(root, "name"),
                GetInt32(root, "type"),
                voiceStates);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SelectVoiceChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var result = await SendCommandCoreAsync(
                "SELECT_VOICE_CHANNEL",
                new { channel_id = channelId, force = false },
                TimeSpan.FromSeconds(30),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> GetSelectedVoiceChannelIdAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var result = await SendCommandCoreAsync(
                "GET_SELECTED_VOICE_CHANNEL",
                new { },
                TimeSpan.FromSeconds(20),
                cancellationToken);
            return GetString(result.RootElement, "id");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            ResetConnection();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }

    internal static byte[] CreateFrame(int opcode, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(8));
        return frame;
    }

    private async Task ConnectCoreAsync(string applicationId, CancellationToken cancellationToken)
    {
        if (_pipe is { IsConnected: true } && string.Equals(_applicationId, applicationId, StringComparison.Ordinal))
        {
            return;
        }

        ResetConnection();
        for (var index = 0; index < 10; index++)
        {
            var candidate = new NamedPipeClientStream(
                ".",
                $"discord-ipc-{index}",
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await candidate.ConnectAsync(200, cancellationToken);
                _pipe = candidate;
                _applicationId = applicationId;
                var handshake = JsonSerializer.Serialize(new { v = RpcVersion, client_id = applicationId }, JsonOptions);
                await WriteFrameCoreAsync(0, handshake, cancellationToken);
                using var ready = await ReadFrameCoreAsync(cancellationToken);
                ThrowIfError(ready.RootElement);
                return;
            }
            catch (TimeoutException)
            {
                candidate.Dispose();
            }
            catch (IOException)
            {
                candidate.Dispose();
            }
        }

        throw new DiscordNotRunningException();
    }

    private async Task<DiscordRpcUser> AuthenticateCoreAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var result = await SendCommandCoreAsync(
            "AUTHENTICATE",
            new { access_token = accessToken },
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (!result.RootElement.TryGetProperty("user", out var userElement))
        {
            throw new DiscordRpcException("Discord authenticated the connection without returning an account.");
        }

        return ParseUser(userElement);
    }

    private async Task<DiscordRpcToken> ExchangeAuthorizationCodeAsync(
        string applicationId,
        string code,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = applicationId,
            ["grant_type"] = "authorization_code",
            ["code"] = code
        });
        return await RequestTokenAsync(content, cancellationToken);
    }

    private async Task<DiscordRpcToken> RequestTokenAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://discord.com/api/oauth2/token")
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "ToolsForSteam/0.4.1-beta.1 (+https://github.com/toonymak1993/tools-for-steam)");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var token = await JsonSerializer.DeserializeAsync<DiscordOAuthTokenResponse>(stream, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(token?.AccessToken))
        {
            var detail = string.IsNullOrWhiteSpace(token?.ErrorDescription)
                ? token?.Error
                : token.ErrorDescription;
            throw new DiscordRpcException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"Discord rejected the authorization token exchange ({(int)response.StatusCode})."
                    : $"Discord rejected the authorization: {detail}. Ensure Public Client is enabled for the Discord application.");
        }

        return new DiscordRpcToken(
            token.AccessToken.Trim(),
            (token.RefreshToken ?? string.Empty).Trim(),
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn)));
    }

    private async Task<JsonDocument> SendCommandCoreAsync(
        string command,
        object arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (_pipe is not { IsConnected: true })
        {
            throw new DiscordRpcException("The local Discord connection is not active.");
        }

        var nonce = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new { cmd = command, args = arguments, nonce }, JsonOptions);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        await WriteFrameCoreAsync(1, payload, timeoutCts.Token);

        while (true)
        {
            var frame = await ReadFrameCoreAsync(timeoutCts.Token);
            try
            {
                var root = frame.RootElement;
                ThrowIfError(root);
                if (!root.TryGetProperty("nonce", out var nonceElement) ||
                    !string.Equals(nonceElement.GetString(), nonce, StringComparison.Ordinal))
                {
                    frame.Dispose();
                    continue;
                }

                if (!root.TryGetProperty("data", out var dataElement))
                {
                    frame.Dispose();
                    throw new DiscordRpcException($"Discord returned an incomplete response for {command}.");
                }

                var result = JsonDocument.Parse(dataElement.GetRawText());
                frame.Dispose();
                return result;
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }
    }

    private async Task WriteFrameCoreAsync(int opcode, string json, CancellationToken cancellationToken)
    {
        var frame = CreateFrame(opcode, json);
        await _pipe!.WriteAsync(frame, cancellationToken);
        await _pipe.FlushAsync(cancellationToken);
    }

    private async Task<JsonDocument> ReadFrameCoreAsync(CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await _pipe!.ReadExactlyAsync(header, cancellationToken);
        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length is < 0 or > MaxFrameBytes)
        {
            throw new DiscordRpcException("Discord returned an invalid local RPC frame.");
        }

        var payload = new byte[length];
        await _pipe.ReadExactlyAsync(payload, cancellationToken);
        var document = JsonDocument.Parse(payload);
        if (opcode == 2)
        {
            var message = GetString(document.RootElement, "message");
            document.Dispose();
            throw new DiscordRpcException(string.IsNullOrWhiteSpace(message) ? "Discord closed the local connection." : message);
        }

        return document;
    }

    private static void ThrowIfError(JsonElement root)
    {
        var eventName = GetString(root, "evt");
        if (!eventName.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var data = root.TryGetProperty("data", out var dataElement) ? dataElement : root;
        var message = GetString(data, "message");
        var code = data.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode)
            ? parsedCode
            : (int?)null;
        throw new DiscordRpcException(
            string.IsNullOrWhiteSpace(message) ? "Discord rejected the local RPC request." : message,
            code);
    }

    private void ResetConnection()
    {
        _pipe?.Dispose();
        _pipe = null;
        _applicationId = string.Empty;
    }

    private static DiscordRpcGuild ParseGuild(JsonElement element)
    {
        return new DiscordRpcGuild(
            GetString(element, "id"),
            string.IsNullOrWhiteSpace(GetString(element, "name")) ? "Discord Server" : GetString(element, "name"),
            GetString(element, "icon_url"));
    }

    private static DiscordRpcChannel ParseChannel(JsonElement element)
    {
        return new DiscordRpcChannel(
            GetString(element, "id"),
            string.IsNullOrWhiteSpace(GetString(element, "name")) ? "Voice channel" : GetString(element, "name"),
            GetInt32(element, "type"),
            GetInt32(element, "position"));
    }

    private static DiscordRpcVoiceState? ParseVoiceState(JsonElement element)
    {
        if (!element.TryGetProperty("user", out var userElement))
        {
            return null;
        }

        return new DiscordRpcVoiceState(
            ParseUser(userElement),
            GetString(element, "nick"),
            GetBoolean(element, "mute") || GetBoolean(element, "self_mute"),
            GetBoolean(element, "deaf") || GetBoolean(element, "self_deaf"));
    }

    private static DiscordRpcUser ParseUser(JsonElement element)
    {
        var username = GetString(element, "username");
        var displayName = GetString(element, "global_name");
        return new DiscordRpcUser(
            GetString(element, "id"),
            username,
            string.IsNullOrWhiteSpace(displayName) ? username : displayName,
            GetString(element, "avatar"));
    }

    private static string GetRequiredString(JsonElement element, string propertyName, string errorMessage)
    {
        var value = GetString(element, propertyName);
        return string.IsNullOrWhiteSpace(value) ? throw new DiscordRpcException(errorMessage) : value;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt32(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static bool GetBoolean(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
               property.GetBoolean();
    }

    private sealed class DiscordOAuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }

        public string? Error { get; init; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; init; }
    }
}
