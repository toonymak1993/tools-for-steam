using System.Runtime.InteropServices;
using System.Text;

namespace SteamLoader.App.Infrastructure.Discord;

internal interface IDiscordSocialSdkClient : IAsyncDisposable
{
    Task<DiscordSocialSession> AuthorizeAsync(string applicationId, CancellationToken cancellationToken);

    Task<DiscordSocialSession> ResumeAsync(
        string applicationId,
        string accessToken,
        string refreshToken,
        DateTimeOffset? expiresAtUtc,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DiscordSocialFriend>> GetFriendsAsync(CancellationToken cancellationToken);

    Task DisconnectAsync();
}

internal sealed record DiscordSocialToken(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAtUtc,
    string Scopes);

internal sealed record DiscordSocialUser(
    string Id,
    string Username,
    string DisplayName,
    string AvatarUrl,
    string Status);

internal sealed record DiscordSocialSession(
    DiscordSocialToken Token,
    DiscordSocialUser User);

internal sealed record DiscordSocialFriend(
    string Id,
    string Username,
    string DisplayName,
    string AvatarUrl,
    string Status);

internal sealed class DiscordSocialSdkException : InvalidOperationException
{
    public DiscordSocialSdkException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thin managed wrapper around Discord Social SDK's stable C ABI. Keeping the bridge in C# avoids
/// requiring a C++ compiler on developer machines while still using Discord's official runtime.
/// </summary>
internal sealed class DiscordSocialSdkClient : IDiscordSocialSdkClient
{
    internal const string RequiredScopes = "openid sdk.social_layer_presence guilds";
    private static readonly NativeMethods.AuthorizationCallback AuthorizationCallback = OnAuthorizationCompleted;
    private static readonly NativeMethods.TokenExchangeCallback TokenExchangeCallback = OnTokenExchangeCompleted;
    private static readonly NativeMethods.UpdateTokenCallback UpdateTokenCallback = OnUpdateTokenCompleted;
    private static readonly NativeMethods.FreeCallback FreeCallback = FreeCallbackState;

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _callbackPumpCts = new();
    private readonly Task _callbackPumpTask;
    private NativeMethods.DiscordObject _client;
    private bool _initialized;
    private bool _disposed;
    private ulong _applicationId;
    private string _activeAccessToken = string.Empty;

    public DiscordSocialSdkClient()
    {
        _callbackPumpTask = Task.Run(() => PumpCallbacksAsync(_callbackPumpCts.Token));
    }

    internal static string GetRuntimeVersion()
    {
        return $"{NativeMethods.Discord_Client_GetVersionMajor()}." +
               $"{NativeMethods.Discord_Client_GetVersionMinor()}." +
               $"{NativeMethods.Discord_Client_GetVersionPatch()}";
    }

    public async Task<DiscordSocialSession> AuthorizeAsync(
        string applicationId,
        CancellationToken cancellationToken)
    {
        var numericApplicationId = ParseApplicationId(applicationId);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            EnsureClient(numericApplicationId);

            var verifier = default(NativeMethods.DiscordObject);
            var challenge = default(NativeMethods.DiscordObject);
            var args = default(NativeMethods.DiscordObject);
            try
            {
                NativeMethods.Discord_Client_CreateAuthorizationCodeVerifier(ref _client, out verifier);
                NativeMethods.Discord_AuthorizationCodeVerifier_Challenge(ref verifier, out challenge);
                var verifierText = ReadOwnedString((ref NativeMethods.NativeString value) =>
                    NativeMethods.Discord_AuthorizationCodeVerifier_Verifier(ref verifier, ref value));

                NativeMethods.Discord_AuthorizationArgs_Init(ref args);
                NativeMethods.Discord_AuthorizationArgs_SetClientId(ref args, numericApplicationId);
                using (var scopes = NativeUtf8String.Create(RequiredScopes))
                {
                    NativeMethods.Discord_AuthorizationArgs_SetScopes(ref args, scopes.Value);
                }

                NativeMethods.Discord_AuthorizationArgs_SetCodeChallenge(ref args, ref challenge);
                var authorizationTask = CreateCallbackTask<AuthorizationResponse>(out var callbackState);
                NativeMethods.Discord_Client_Authorize(
                    ref _client,
                    ref args,
                    AuthorizationCallback,
                    FreeCallback,
                    callbackState);

                var authorization = await authorizationTask.WaitAsync(cancellationToken);
                var token = await ExchangeCodeAsync(
                    numericApplicationId,
                    authorization.Code,
                    verifierText,
                    authorization.RedirectUri,
                    cancellationToken);
                return await ActivateTokenAsync(token, cancellationToken);
            }
            finally
            {
                DropAuthorizationObject(ref args, NativeMethods.Discord_AuthorizationArgs_Drop);
                DropAuthorizationObject(ref challenge, NativeMethods.Discord_AuthorizationCodeChallenge_Drop);
                DropAuthorizationObject(ref verifier, NativeMethods.Discord_AuthorizationCodeVerifier_Drop);
            }
        }
        catch (DllNotFoundException exception)
        {
            throw new DiscordSocialSdkException(
                "Discord Social SDK is not installed with Tools for Steam. Reinstall or repair the application.",
                exception);
        }
        catch (BadImageFormatException exception)
        {
            throw new DiscordSocialSdkException(
                "Discord Social SDK has the wrong architecture. The Windows x64 runtime is required.",
                exception);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<DiscordSocialSession> ResumeAsync(
        string applicationId,
        string accessToken,
        string refreshToken,
        DateTimeOffset? expiresAtUtc,
        CancellationToken cancellationToken)
    {
        var numericApplicationId = ParseApplicationId(applicationId);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            EnsureClient(numericApplicationId);
            var token = new DiscordSocialToken(
                accessToken,
                refreshToken,
                expiresAtUtc ?? DateTimeOffset.UtcNow.AddDays(7),
                RequiredScopes);

            if (token.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddHours(24))
            {
                if (string.IsNullOrWhiteSpace(refreshToken))
                {
                    throw new DiscordSocialSdkException("Your Discord session expired. Connect Discord again.");
                }

                token = await RefreshTokenAsync(numericApplicationId, refreshToken, cancellationToken);
            }

            if (string.Equals(_activeAccessToken, token.AccessToken, StringComparison.Ordinal) &&
                NativeMethods.Discord_Client_GetStatus(ref _client) == NativeMethods.ClientStatus.Ready)
            {
                return new DiscordSocialSession(token, ReadCurrentUser());
            }

            return await ActivateTokenAsync(token, cancellationToken);
        }
        catch (DllNotFoundException exception)
        {
            throw new DiscordSocialSdkException(
                "Discord Social SDK is not installed with Tools for Steam. Reinstall or repair the application.",
                exception);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<IReadOnlyList<DiscordSocialFriend>> GetFriendsAsync(
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!_initialized || NativeMethods.Discord_Client_GetStatus(ref _client) != NativeMethods.ClientStatus.Ready)
            {
                throw new DiscordSocialSdkException("Discord is not connected yet.");
            }

            var friends = new Dictionary<string, DiscordSocialFriend>(StringComparer.Ordinal);
            ReadRelationshipGroup(NativeMethods.RelationshipGroup.OnlinePlayingGame, friends);
            ReadRelationshipGroup(NativeMethods.RelationshipGroup.OnlineElsewhere, friends);
            ReadRelationshipGroup(NativeMethods.RelationshipGroup.Offline, friends);
            return friends.Values
                .OrderBy(friend => GetStatusRank(friend.Status))
                .ThenBy(friend => friend.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            if (_initialized)
            {
                NativeMethods.Discord_Client_Disconnect(ref _client);
                _activeAccessToken = string.Empty;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _callbackPumpCts.CancelAsync();
        try
        {
            await _callbackPumpTask;
        }
        catch (OperationCanceledException)
        {
        }

        await _operationGate.WaitAsync();
        try
        {
            if (_initialized)
            {
                NativeMethods.Discord_Client_Disconnect(ref _client);
                NativeMethods.Discord_Client_Drop(ref _client);
                _initialized = false;
            }
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
            _callbackPumpCts.Dispose();
        }
    }

    private void EnsureClient(ulong applicationId)
    {
        ThrowIfDisposed();
        if (_initialized && _applicationId == applicationId)
        {
            return;
        }

        if (_initialized)
        {
            NativeMethods.Discord_Client_Disconnect(ref _client);
            NativeMethods.Discord_Client_Drop(ref _client);
        }

        _client = default;
        NativeMethods.Discord_Client_Init(ref _client);
        NativeMethods.Discord_Client_SetApplicationId(ref _client, applicationId);
        _applicationId = applicationId;
        _activeAccessToken = string.Empty;
        _initialized = true;
    }

    private async Task<DiscordSocialToken> ExchangeCodeAsync(
        ulong applicationId,
        string code,
        string verifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var tokenTask = CreateCallbackTask<TokenResponse>(out var callbackState);
        using var codeValue = NativeUtf8String.Create(code);
        using var verifierValue = NativeUtf8String.Create(verifier);
        using var redirectValue = NativeUtf8String.Create(redirectUri);
        NativeMethods.Discord_Client_GetToken(
            ref _client,
            applicationId,
            codeValue.Value,
            verifierValue.Value,
            redirectValue.Value,
            TokenExchangeCallback,
            FreeCallback,
            callbackState);
        var response = await tokenTask.WaitAsync(cancellationToken);
        return response.ToToken();
    }

    private async Task<DiscordSocialToken> RefreshTokenAsync(
        ulong applicationId,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var tokenTask = CreateCallbackTask<TokenResponse>(out var callbackState);
        using var refreshValue = NativeUtf8String.Create(refreshToken);
        NativeMethods.Discord_Client_RefreshToken(
            ref _client,
            applicationId,
            refreshValue.Value,
            TokenExchangeCallback,
            FreeCallback,
            callbackState);
        var response = await tokenTask.WaitAsync(cancellationToken);
        return response.ToToken();
    }

    private async Task<DiscordSocialSession> ActivateTokenAsync(
        DiscordSocialToken token,
        CancellationToken cancellationToken)
    {
        var updateTask = CreateCallbackTask<bool>(out var callbackState);
        using (var accessValue = NativeUtf8String.Create(token.AccessToken))
        {
            NativeMethods.Discord_Client_UpdateToken(
                ref _client,
                NativeMethods.AuthorizationTokenType.Bearer,
                accessValue.Value,
                UpdateTokenCallback,
                FreeCallback,
                callbackState);
        }

        await updateTask.WaitAsync(cancellationToken);
        NativeMethods.Discord_Client_Connect(ref _client);
        await WaitForReadyAsync(cancellationToken);
        _activeAccessToken = token.AccessToken;
        return new DiscordSocialSession(token, ReadCurrentUser());
    }

    private async Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = NativeMethods.Discord_Client_GetStatus(ref _client);
            if (status == NativeMethods.ClientStatus.Ready)
            {
                return;
            }

            if (status == NativeMethods.ClientStatus.Disconnected)
            {
                throw new DiscordSocialSdkException(
                    "Discord could not establish the Social SDK connection. Check the application approval and try again.");
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new DiscordSocialSdkException("Discord did not finish connecting within 30 seconds.");
    }

    private DiscordSocialUser ReadCurrentUser()
    {
        var user = default(NativeMethods.DiscordObject);
        if (!NativeMethods.Discord_Client_GetCurrentUserV2(ref _client, ref user))
        {
            throw new DiscordSocialSdkException("Discord connected without returning the signed-in account.");
        }

        try
        {
            return ReadUser(ref user);
        }
        finally
        {
            NativeMethods.Discord_UserHandle_Drop(ref user);
        }
    }

    private void ReadRelationshipGroup(
        NativeMethods.RelationshipGroup group,
        IDictionary<string, DiscordSocialFriend> friends)
    {
        NativeMethods.Discord_Client_GetRelationshipsByGroup(ref _client, group, out var span);
        try
        {
            var count = checked((int)span.Size);
            for (var index = 0; index < count; index++)
            {
                var relationship = new NativeMethods.DiscordObject
                {
                    Opaque = Marshal.ReadIntPtr(span.Pointer, index * IntPtr.Size)
                };
                try
                {
                    if (NativeMethods.Discord_RelationshipHandle_DiscordRelationshipType(ref relationship) !=
                        NativeMethods.RelationshipType.Friend)
                    {
                        continue;
                    }

                    var user = default(NativeMethods.DiscordObject);
                    if (!NativeMethods.Discord_RelationshipHandle_User(ref relationship, ref user))
                    {
                        continue;
                    }

                    try
                    {
                        var value = ReadUser(ref user);
                        friends[value.Id] = new DiscordSocialFriend(
                            value.Id,
                            value.Username,
                            value.DisplayName,
                            value.AvatarUrl,
                            value.Status);
                    }
                    finally
                    {
                        NativeMethods.Discord_UserHandle_Drop(ref user);
                    }
                }
                finally
                {
                    NativeMethods.Discord_RelationshipHandle_Drop(ref relationship);
                }
            }
        }
        finally
        {
            if (span.Pointer != IntPtr.Zero)
            {
                NativeMethods.Discord_Free(span.Pointer);
            }
        }
    }

    private static DiscordSocialUser ReadUser(ref NativeMethods.DiscordObject user)
    {
        var id = NativeMethods.Discord_UserHandle_Id(ref user).ToString();
        var usernameValue = default(NativeMethods.NativeString);
        NativeMethods.Discord_UserHandle_Username(ref user, ref usernameValue);
        var username = ReadAndFreeString(usernameValue);
        var displayNameValue = default(NativeMethods.NativeString);
        NativeMethods.Discord_UserHandle_DisplayName(ref user, ref displayNameValue);
        var displayName = ReadAndFreeString(displayNameValue);
        var avatarValue = default(NativeMethods.NativeString);
        NativeMethods.Discord_UserHandle_AvatarUrl(
            ref user,
            NativeMethods.AvatarType.Gif,
            NativeMethods.AvatarType.Webp,
            ref avatarValue);
        var avatarUrl = ReadAndFreeString(avatarValue);
        var status = NativeMethods.Discord_UserHandle_Status(ref user) switch
        {
            NativeMethods.StatusType.Online => "online",
            NativeMethods.StatusType.Idle => "idle",
            NativeMethods.StatusType.DoNotDisturb => "dnd",
            NativeMethods.StatusType.Streaming => "streaming",
            _ => "offline"
        };
        return new DiscordSocialUser(
            id,
            username,
            string.IsNullOrWhiteSpace(displayName) ? username : displayName,
            avatarUrl,
            status);
    }

    private static async Task PumpCallbacksAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                NativeMethods.Discord_RunCallbacks();
            }
            catch (DllNotFoundException)
            {
                // The SDK is loaded lazily. A missing runtime is reported when the user connects.
            }
        }
    }

    private static Task<T> CreateCallbackTask<T>(out IntPtr callbackState)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        callbackState = GCHandle.ToIntPtr(GCHandle.Alloc(new CallbackState<T>(completion)));
        return completion.Task;
    }

    private static void OnAuthorizationCompleted(
        IntPtr result,
        NativeMethods.NativeString code,
        NativeMethods.NativeString redirectUri,
        IntPtr userData)
    {
        var completion = GetCallbackState<AuthorizationResponse>(userData).Completion;
        try
        {
            EnsureSuccessful(result, "Discord authorization failed");
            completion.TrySetResult(new AuthorizationResponse(
                ReadString(code),
                ReadString(redirectUri)));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            FreeString(code);
            FreeString(redirectUri);
            NativeMethods.Discord_ClientResult_Drop(result);
        }
    }

    private static void OnTokenExchangeCompleted(
        IntPtr result,
        NativeMethods.NativeString accessToken,
        NativeMethods.NativeString refreshToken,
        NativeMethods.AuthorizationTokenType tokenType,
        int expiresIn,
        NativeMethods.NativeString scopes,
        IntPtr userData)
    {
        var completion = GetCallbackState<TokenResponse>(userData).Completion;
        try
        {
            EnsureSuccessful(result, "Discord token exchange failed");
            completion.TrySetResult(new TokenResponse(
                ReadString(accessToken),
                ReadString(refreshToken),
                Math.Max(expiresIn, 60),
                ReadString(scopes)));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            FreeString(accessToken);
            FreeString(refreshToken);
            FreeString(scopes);
            NativeMethods.Discord_ClientResult_Drop(result);
        }
    }

    private static void OnUpdateTokenCompleted(IntPtr result, IntPtr userData)
    {
        var completion = GetCallbackState<bool>(userData).Completion;
        try
        {
            EnsureSuccessful(result, "Discord rejected the account token");
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            NativeMethods.Discord_ClientResult_Drop(result);
        }
    }

    private static void EnsureSuccessful(IntPtr result, string prefix)
    {
        if (result != IntPtr.Zero && NativeMethods.Discord_ClientResult_Successful(result))
        {
            return;
        }

        var detail = result == IntPtr.Zero
            ? string.Empty
            : ReadOwnedString((ref NativeMethods.NativeString value) =>
                NativeMethods.Discord_ClientResult_Error(result, ref value));
        throw new DiscordSocialSdkException(
            string.IsNullOrWhiteSpace(detail) ? $"{prefix}." : $"{prefix}: {detail}");
    }

    private static void FreeCallbackState(IntPtr userData)
    {
        if (userData == IntPtr.Zero)
        {
            return;
        }

        var handle = GCHandle.FromIntPtr(userData);
        if (handle.IsAllocated)
        {
            handle.Free();
        }
    }

    private static CallbackState<T> GetCallbackState<T>(IntPtr userData)
    {
        return GCHandle.FromIntPtr(userData).Target as CallbackState<T>
            ?? throw new DiscordSocialSdkException("Discord returned an invalid callback state.");
    }

    private static string ReadOwnedString(NativeStringReader reader)
    {
        var value = default(NativeMethods.NativeString);
        reader(ref value);
        return ReadAndFreeString(value);
    }

    private static string ReadAndFreeString(NativeMethods.NativeString value)
    {
        try
        {
            return ReadString(value);
        }
        finally
        {
            FreeString(value);
        }
    }

    private static string ReadString(NativeMethods.NativeString value)
    {
        if (value.Pointer == IntPtr.Zero || value.Size == 0)
        {
            return string.Empty;
        }

        var length = checked((int)value.Size);
        var bytes = new byte[length];
        Marshal.Copy(value.Pointer, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void FreeString(NativeMethods.NativeString value)
    {
        if (value.Pointer != IntPtr.Zero)
        {
            NativeMethods.Discord_Free(value.Pointer);
        }
    }

    private static void DropAuthorizationObject(
        ref NativeMethods.DiscordObject value,
        NativeObjectDropper dropper)
    {
        if (value.Opaque != IntPtr.Zero)
        {
            dropper(ref value);
        }
    }

    private static ulong ParseApplicationId(string applicationId)
    {
        return ulong.TryParse(applicationId, out var value) && value > 0
            ? value
            : throw new DiscordSocialSdkException("The embedded Discord application ID is invalid.");
    }

    private static int GetStatusRank(string status) => status switch
    {
        "online" => 0,
        "streaming" => 1,
        "idle" => 2,
        "dnd" => 3,
        _ => 4
    };

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private delegate void NativeStringReader(ref NativeMethods.NativeString value);

    private delegate void NativeObjectDropper(ref NativeMethods.DiscordObject value);

    private sealed record CallbackState<T>(TaskCompletionSource<T> Completion);

    private sealed record AuthorizationResponse(string Code, string RedirectUri);

    private sealed record TokenResponse(
        string AccessToken,
        string RefreshToken,
        int ExpiresInSeconds,
        string Scopes)
    {
        public DiscordSocialToken ToToken() => new(
            AccessToken,
            RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(ExpiresInSeconds),
            Scopes);
    }

    private sealed class NativeUtf8String : IDisposable
    {
        private NativeUtf8String(IntPtr pointer, nuint size)
        {
            Value = new NativeMethods.NativeString { Pointer = pointer, Size = size };
        }

        public NativeMethods.NativeString Value { get; }

        public static NativeUtf8String Create(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            var pointer = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
            if (bytes.Length > 0)
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
            }

            return new NativeUtf8String(pointer, (nuint)bytes.Length);
        }

        public void Dispose() => Marshal.FreeHGlobal(Value.Pointer);
    }

    private static class NativeMethods
    {
        private const string LibraryName = "discord_partner_sdk";

        internal enum AuthorizationTokenType
        {
            User = 0,
            Bearer = 1
        }

        internal enum ClientStatus
        {
            Disconnected = 0,
            Connecting = 1,
            Connected = 2,
            Ready = 3,
            Reconnecting = 4,
            Disconnecting = 5,
            HttpWait = 6
        }

        internal enum RelationshipGroup
        {
            OnlinePlayingGame = 0,
            OnlineElsewhere = 1,
            Offline = 2
        }

        internal enum RelationshipType
        {
            None = 0,
            Friend = 1,
            Blocked = 2,
            PendingIncoming = 3,
            PendingOutgoing = 4,
            Implicit = 5,
            Suggestion = 6
        }

        internal enum AvatarType
        {
            Gif = 0,
            Webp = 1,
            Png = 2,
            Jpeg = 3
        }

        internal enum StatusType
        {
            Online = 0,
            Offline = 1,
            Blocked = 2,
            Idle = 3,
            DoNotDisturb = 4,
            Invisible = 5,
            Streaming = 6,
            Unknown = 7
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DiscordObject
        {
            public IntPtr Opaque;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeString
        {
            public IntPtr Pointer;
            public nuint Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeSpan
        {
            public IntPtr Pointer;
            public nuint Size;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void FreeCallback(IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void AuthorizationCallback(
            IntPtr result,
            NativeString code,
            NativeString redirectUri,
            IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void TokenExchangeCallback(
            IntPtr result,
            NativeString accessToken,
            NativeString refreshToken,
            AuthorizationTokenType tokenType,
            int expiresIn,
            NativeString scopes,
            IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void UpdateTokenCallback(IntPtr result, IntPtr userData);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_RunCallbacks();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Discord_Client_GetVersionMajor();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Discord_Client_GetVersionMinor();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Discord_Client_GetVersionPatch();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_Free(IntPtr pointer);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_Client_Init(ref DiscordObject client);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_Client_Drop(ref DiscordObject client);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_Client_SetApplicationId(ref DiscordObject client, ulong applicationId);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_Client_CreateAuthorizationCodeVerifier(
            ref DiscordObject client,
            out DiscordObject verifier);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_AuthorizationCodeVerifier_Challenge(
            ref DiscordObject verifier,
            out DiscordObject challenge);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_AuthorizationCodeVerifier_Verifier(
            ref DiscordObject verifier,
            ref NativeString returnValue);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_AuthorizationCodeVerifier_Drop(ref DiscordObject verifier);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_AuthorizationCodeChallenge_Drop(ref DiscordObject challenge);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_AuthorizationArgs_Init(ref DiscordObject args);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_AuthorizationArgs_Drop(ref DiscordObject args);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_AuthorizationArgs_SetClientId(ref DiscordObject args, ulong clientId);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_AuthorizationArgs_SetScopes(ref DiscordObject args, NativeString scopes);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_AuthorizationArgs_SetCodeChallenge(
            ref DiscordObject args,
            ref DiscordObject challenge);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_Client_Authorize(
            ref DiscordObject client,
            ref DiscordObject args,
            AuthorizationCallback callback,
            FreeCallback callbackDataFree,
            IntPtr callbackData);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_Client_GetToken(
            ref DiscordObject client,
            ulong applicationId,
            NativeString code,
            NativeString codeVerifier,
            NativeString redirectUri,
            TokenExchangeCallback callback,
            FreeCallback callbackDataFree,
            IntPtr callbackData);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_Client_RefreshToken(
            ref DiscordObject client,
            ulong applicationId,
            NativeString refreshToken,
            TokenExchangeCallback callback,
            FreeCallback callbackDataFree,
            IntPtr callbackData);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_Client_UpdateToken(
            ref DiscordObject client,
            AuthorizationTokenType tokenType,
            NativeString token,
            UpdateTokenCallback callback,
            FreeCallback callbackDataFree,
            IntPtr callbackData);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_Client_Connect(ref DiscordObject client);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_Client_Disconnect(ref DiscordObject client);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ClientStatus Discord_Client_GetStatus(ref DiscordObject client);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Discord_Client_GetCurrentUserV2(
            ref DiscordObject client,
            ref DiscordObject returnValue);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_Client_GetRelationshipsByGroup(
            ref DiscordObject client,
            RelationshipGroup group,
            out NativeSpan returnValue);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern RelationshipType Discord_RelationshipHandle_DiscordRelationshipType(
            ref DiscordObject relationship);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Discord_RelationshipHandle_User(
            ref DiscordObject relationship,
            ref DiscordObject returnValue);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_RelationshipHandle_Drop(ref DiscordObject relationship);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_UserHandle_Drop(ref DiscordObject user);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong Discord_UserHandle_Id(ref DiscordObject user);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_UserHandle_Username(
            ref DiscordObject user,
            ref NativeString returnValue);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_UserHandle_DisplayName(
            ref DiscordObject user,
            ref NativeString returnValue);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_UserHandle_AvatarUrl(
            ref DiscordObject user,
            AvatarType animatedType,
            AvatarType staticType,
            ref NativeString returnValue);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern StatusType Discord_UserHandle_Status(ref DiscordObject user);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Discord_ClientResult_Successful(IntPtr result);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_ClientResult_Error(IntPtr result, ref NativeString returnValue);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Discord_ClientResult_Drop(IntPtr result);
    }
}
