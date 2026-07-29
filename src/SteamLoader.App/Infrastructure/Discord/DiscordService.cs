using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.Discord;

public sealed class DiscordService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> InviteHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord.gg",
        "discord.com",
        "www.discord.com",
        "discordapp.com",
        "www.discordapp.com"
    };
    private static readonly HashSet<string> AvatarHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.discordapp.com",
        "cdn.discord.com",
        "media.discordapp.net"
    };

    private readonly HttpClient _httpClient;
    private readonly DiscordSettingsStore _settingsStore;
    private readonly IDiscordRpcClient _rpcClient;
    private readonly IDiscordSocialSdkClient? _socialClient;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private DiscordSnapshot? _cachedSnapshot;
    private DateTimeOffset _cachedAtUtc = DateTimeOffset.MinValue;

    public DiscordService(HttpClient httpClient, DiscordSettingsStore settingsStore)
        : this(httpClient, settingsStore, new DiscordRpcClient(httpClient), new DiscordSocialSdkClient())
    {
    }

    internal DiscordService(
        HttpClient httpClient,
        DiscordSettingsStore settingsStore,
        IDiscordRpcClient rpcClient)
        : this(httpClient, settingsStore, rpcClient, socialClient: null)
    {
    }

    internal DiscordService(
        HttpClient httpClient,
        DiscordSettingsStore settingsStore,
        IDiscordRpcClient rpcClient,
        IDiscordSocialSdkClient? socialClient)
    {
        _httpClient = httpClient;
        _settingsStore = settingsStore;
        _rpcClient = rpcClient;
        _socialClient = socialClient;
    }

    public async Task<DiscordSnapshot> GetSnapshotAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var configuration = _settingsStore.Load();
            var applicationId = ResolveApplicationId(configuration);
            if (!string.IsNullOrWhiteSpace(applicationId))
            {
                if (_socialClient is not null)
                {
                    return ApplyConfiguration(
                        await FetchSocialSnapshotAsync(configuration, applicationId, cancellationToken),
                        configuration);
                }

                return ApplyConfiguration(
                    await FetchRpcSnapshotAsync(configuration, applicationId, cancellationToken),
                    configuration);
            }

            if (!forceRefresh &&
                _cachedSnapshot is not null &&
                string.Equals(_cachedSnapshot.ServerId, configuration.ServerId, StringComparison.Ordinal) &&
                string.Equals(_cachedSnapshot.ConfiguredInviteUrl, configuration.InviteUrl, StringComparison.Ordinal) &&
                DateTimeOffset.UtcNow - _cachedAtUtc < CacheLifetime)
            {
                return _cachedSnapshot;
            }

            var snapshot = ApplyConfiguration(
                await FetchSnapshotAsync(configuration, cancellationToken),
                configuration);
            _cachedSnapshot = snapshot;
            _cachedAtUtc = DateTimeOffset.UtcNow;
            return snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<DiscordSnapshot> GetWidgetFallbackSnapshotAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var configuration = _settingsStore.Load();
            return ApplyConfiguration(
                await FetchSnapshotAsync(configuration, cancellationToken),
                configuration);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<DiscordSnapshot> SaveSettingsAsync(
        string? applicationId,
        string? serverId,
        string? inviteUrl,
        CancellationToken cancellationToken)
    {
        var existing = _settingsStore.Load();
        var normalizedApplicationId = NormalizeOptionalSnowflake(applicationId, "Discord application ID");
        var normalizedServerId = NormalizeOptionalSnowflake(serverId, "Discord server ID");
        var normalizedInviteUrl = NormalizeInviteUrl(inviteUrl);
        var applicationChanged = !string.Equals(
            existing.ApplicationId,
            normalizedApplicationId,
            StringComparison.Ordinal);
        _settingsStore.Save(new DiscordConfiguration
        {
            ApplicationId = normalizedApplicationId,
            ServerId = normalizedServerId,
            InviteUrl = normalizedInviteUrl,
            AccessToken = applicationChanged ? string.Empty : existing.AccessToken,
            RefreshToken = applicationChanged ? string.Empty : existing.RefreshToken,
            TokenExpiresAtUtc = applicationChanged ? null : existing.TokenExpiresAtUtc,
            SelectedGuildId = applicationChanged ? string.Empty : existing.SelectedGuildId,
            TokenProvider = applicationChanged ? string.Empty : existing.TokenProvider,
            TokenScopes = applicationChanged ? string.Empty : existing.TokenScopes,
            FavoriteGuildIds = [.. existing.FavoriteGuildIds],
            FriendOnlineNotificationsEnabled = existing.FriendOnlineNotificationsEnabled
        });
        if (applicationChanged)
        {
            await _rpcClient.DisconnectAsync();
            if (_socialClient is not null)
            {
                await _socialClient.DisconnectAsync();
            }
        }

        InvalidateCache();
        return await GetSnapshotAsync(forceRefresh: true, cancellationToken);
    }

    public async Task<DiscordSnapshot> ConnectAsync(CancellationToken cancellationToken)
    {
        var configuration = _settingsStore.Load();
        var applicationId = ResolveApplicationId(configuration);
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            throw new InvalidOperationException(
                "This build has no Discord application ID. Add the Tools for Steam application ID in Discord Settings first.");
        }

        if (_socialClient is not null)
        {
            var session = await _socialClient.AuthorizeAsync(applicationId, cancellationToken);
            configuration.ApplicationId = applicationId;
            configuration.AccessToken = session.Token.AccessToken;
            configuration.RefreshToken = session.Token.RefreshToken;
            configuration.TokenExpiresAtUtc = session.Token.ExpiresAtUtc;
            configuration.SelectedGuildId = string.Empty;
            configuration.TokenProvider = "social-sdk";
            configuration.TokenScopes = session.Token.Scopes;
            _settingsStore.Save(configuration);
            InvalidateCache();
            return await GetSnapshotAsync(forceRefresh: true, cancellationToken);
        }

        var authentication = await _rpcClient.AuthorizeAsync(applicationId, cancellationToken);
        configuration.ApplicationId = applicationId;
        configuration.AccessToken = authentication.Token.AccessToken;
        configuration.RefreshToken = authentication.Token.RefreshToken;
        configuration.TokenExpiresAtUtc = authentication.Token.ExpiresAtUtc;
        configuration.SelectedGuildId = string.Empty;
        configuration.TokenProvider = "rpc";
        configuration.TokenScopes = "rpc identify";
        _settingsStore.Save(configuration);
        InvalidateCache();
        return await GetSnapshotAsync(forceRefresh: true, cancellationToken);
    }

    public async Task<DiscordSnapshot> DisconnectAsync(CancellationToken cancellationToken)
    {
        var configuration = _settingsStore.Load();
        configuration.AccessToken = string.Empty;
        configuration.RefreshToken = string.Empty;
        configuration.TokenExpiresAtUtc = null;
        configuration.SelectedGuildId = string.Empty;
        configuration.TokenProvider = string.Empty;
        configuration.TokenScopes = string.Empty;
        _settingsStore.Save(configuration);
        await _rpcClient.DisconnectAsync();
        if (_socialClient is not null)
        {
            await _socialClient.DisconnectAsync();
        }
        InvalidateCache();
        return await GetSnapshotAsync(forceRefresh: true, cancellationToken);
    }

    public async Task<DiscordSnapshot> SelectGuildAsync(
        string? guildId,
        CancellationToken cancellationToken)
    {
        var normalizedGuildId = NormalizeOptionalSnowflake(guildId, "Discord server ID");
        var configuration = _settingsStore.Load();
        configuration.SelectedGuildId = normalizedGuildId;
        _settingsStore.Save(configuration);
        InvalidateCache();
        return await GetSnapshotAsync(forceRefresh: true, cancellationToken);
    }

    public async Task<DiscordSnapshot> SetGuildFavoriteAsync(
        string? guildId,
        bool favorite,
        CancellationToken cancellationToken)
    {
        var normalizedGuildId = NormalizeOptionalSnowflake(guildId, "Discord server ID");
        if (string.IsNullOrWhiteSpace(normalizedGuildId))
        {
            throw new InvalidOperationException("A Discord server is required.");
        }

        var configuration = _settingsStore.Load();
        var favoriteGuildIds = configuration.FavoriteGuildIds.ToHashSet(StringComparer.Ordinal);
        if (favorite)
        {
            favoriteGuildIds.Add(normalizedGuildId);
        }
        else
        {
            favoriteGuildIds.Remove(normalizedGuildId);
        }

        configuration.FavoriteGuildIds = [.. favoriteGuildIds];
        _settingsStore.Save(configuration);
        InvalidateCache();
        return await GetSnapshotAsync(forceRefresh: true, cancellationToken);
    }

    public async Task<DiscordSnapshot> SetFriendOnlineNotificationsAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        var configuration = _settingsStore.Load();
        configuration.FriendOnlineNotificationsEnabled = enabled;
        _settingsStore.Save(configuration);
        InvalidateCache();
        return await GetSnapshotAsync(forceRefresh: true, cancellationToken);
    }

    public bool AreFriendOnlineNotificationsEnabled()
    {
        return _settingsStore.Load().FriendOnlineNotificationsEnabled;
    }

    public async Task<DiscordSnapshot> JoinVoiceChannelAsync(
        string? channelId,
        CancellationToken cancellationToken)
    {
        var normalizedChannelId = NormalizeOptionalSnowflake(channelId, "Discord voice channel ID");
        if (string.IsNullOrWhiteSpace(normalizedChannelId))
        {
            throw new InvalidOperationException("A Discord voice channel is required.");
        }

        var snapshot = await GetSnapshotAsync(forceRefresh: true, cancellationToken);
        var channel = (snapshot.VoiceChannels ?? [])
            .FirstOrDefault(candidate => string.Equals(candidate.Id, normalizedChannelId, StringComparison.Ordinal));
        if (channel is null)
        {
            throw new InvalidOperationException("The selected Discord voice channel is not available on this server.");
        }

        await _rpcClient.SelectVoiceChannelAsync(normalizedChannelId, cancellationToken);
        InvalidateCache();
        return await GetSnapshotAsync(forceRefresh: true, cancellationToken);
    }

    public async Task<string> OpenGuildAsync(string? guildId, CancellationToken cancellationToken)
    {
        var normalizedGuildId = NormalizeOptionalSnowflake(guildId, "Discord server ID");
        if (string.IsNullOrWhiteSpace(normalizedGuildId))
        {
            throw new InvalidOperationException("A Discord server is required.");
        }

        var snapshot = await GetSnapshotAsync(forceRefresh: false, cancellationToken);
        if (!(snapshot.Guilds ?? []).Any(guild =>
                string.Equals(guild.Id, normalizedGuildId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The selected Discord server is not available for this account.");
        }

        var target = $"discord://-/channels/{normalizedGuildId}";
        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        })?.Dispose();
        await BringDiscordToForegroundAsync(cancellationToken);
        return target;
    }

    public async Task<DiscordSnapshot> ClearSettingsAsync(CancellationToken cancellationToken)
    {
        _settingsStore.Clear();
        await _rpcClient.DisconnectAsync();
        if (_socialClient is not null)
        {
            await _socialClient.DisconnectAsync();
        }
        InvalidateCache();
        return await GetSnapshotAsync(forceRefresh: true, cancellationToken);
    }

    public async Task<string> OpenServerAsync(CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(forceRefresh: false, cancellationToken);
        var inviteUrl = NormalizeInviteUrl(snapshot.InviteUrl);
        if (string.IsNullOrWhiteSpace(inviteUrl))
        {
            throw new InvalidOperationException(
                "No Discord invite is available. Add an invite URL or select an invite channel in the Discord server widget settings.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = inviteUrl,
            UseShellExecute = true
        })?.Dispose();

        return inviteUrl;
    }

    public async ValueTask DisposeAsync()
    {
        _refreshGate.Dispose();
        await _rpcClient.DisposeAsync();
        if (_socialClient is not null)
        {
            await _socialClient.DisposeAsync();
        }
    }

    internal static string NormalizeServerId(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("A Discord server ID is required.");
        }

        if (normalized.Length is < 16 or > 32 ||
            !normalized.All(char.IsAsciiDigit) ||
            !ulong.TryParse(normalized, out var parsed) ||
            parsed == 0)
        {
            throw new InvalidOperationException("The Discord server ID must be a valid numeric snowflake.");
        }

        return normalized;
    }

    internal static string NormalizeApplicationId(string? value)
    {
        var normalized = NormalizeOptionalSnowflake(value, "Discord application ID");
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("A Discord application ID is required.");
        }

        return normalized;
    }

    internal static string NormalizeInviteUrl(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            if (!IsSafeInviteCode(normalized))
            {
                throw new InvalidOperationException("The Discord invite code contains unsupported characters.");
            }

            return $"https://discord.gg/{normalized}";
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !InviteHosts.Contains(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("Only secure discord.gg or discord.com invite URLs are supported.");
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var code = segments.Length switch
        {
            1 when uri.Host.Equals("discord.gg", StringComparison.OrdinalIgnoreCase) => segments[0],
            2 when segments[0].Equals("invite", StringComparison.OrdinalIgnoreCase) => segments[1],
            _ => string.Empty
        };

        if (!IsSafeInviteCode(code))
        {
            throw new InvalidOperationException("The Discord invite URL does not contain a valid invite code.");
        }

        return $"https://discord.gg/{code}";
    }

    private static DiscordSnapshot ApplyConfiguration(
        DiscordSnapshot snapshot,
        DiscordConfiguration configuration)
    {
        var favoriteGuildIds = configuration.FavoriteGuildIds.ToHashSet(StringComparer.Ordinal);
        var guilds = snapshot.Guilds?
            .Select(guild => guild with { IsFavorite = favoriteGuildIds.Contains(guild.Id) })
            .OrderByDescending(guild => guild.IsFavorite)
            .ThenBy(guild => guild.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return snapshot with
        {
            Guilds = guilds,
            FriendOnlineNotificationsEnabled = configuration.FriendOnlineNotificationsEnabled
        };
    }

    private async Task<DiscordSnapshot> FetchSnapshotAsync(
        DiscordConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.ServerId))
        {
            return BuildUnconfiguredSnapshot();
        }

        string serverId;
        string configuredInviteUrl;
        try
        {
            serverId = NormalizeServerId(configuration.ServerId);
            configuredInviteUrl = NormalizeInviteUrl(configuration.InviteUrl);
        }
        catch (InvalidOperationException exception)
        {
            return BuildErrorSnapshot(configuration, exception.Message);
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://discord.com/api/v10/guilds/{serverId}/widget.json");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "ToolsForSteam/0.4.1-beta.1 (+https://github.com/toonymak1993/tools-for-steam)");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var discordError = response.IsSuccessStatusCode
                ? null
                : await TryReadDiscordErrorAsync(response, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Forbidden && discordError?.Code == 50004)
            {
                return BuildErrorSnapshot(
                    configuration,
                    "The Discord server widget is disabled. In Discord, open Server Settings > Engagement (Beteiligung), scroll down, enable Server Widget, select an invite channel, then refresh here.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden && discordError?.Code == 40333)
            {
                return BuildErrorSnapshot(
                    configuration,
                    "Discord's Cloudflare protection blocked the widget request. Wait a moment, then refresh again.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return BuildErrorSnapshot(
                    configuration,
                    "Discord could not expose this server. Open Server Settings > Engagement (Beteiligung), enable Server Widget, and verify the server ID.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return BuildErrorSnapshot(
                    configuration,
                    string.IsNullOrWhiteSpace(discordError?.Message)
                        ? $"Discord returned {(int)response.StatusCode} while loading the server widget."
                        : $"Discord returned {(int)response.StatusCode}: {discordError.Message}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var widget = await JsonSerializer.DeserializeAsync<DiscordWidgetResponse>(
                stream,
                JsonOptions,
                cancellationToken);
            if (widget is null)
            {
                return BuildErrorSnapshot(configuration, "Discord returned an empty server widget response.");
            }

            var channels = widget.Channels
                .Where(channel => !string.IsNullOrWhiteSpace(channel.Id))
                .OrderBy(channel => channel.Position)
                .ThenBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
                .Select(channel => new DiscordChannelState(
                    channel.Id!.Trim(),
                    string.IsNullOrWhiteSpace(channel.Name) ? "Voice channel" : channel.Name.Trim()))
                .ToArray();
            var channelNames = channels.ToDictionary(channel => channel.Id, channel => channel.Name, StringComparer.Ordinal);
            var members = widget.Members
                .Where(member => !string.IsNullOrWhiteSpace(member.Username))
                .Select(member => new DiscordMemberState(
                    (member.Id ?? string.Empty).Trim(),
                    member.Username!.Trim(),
                    NormalizeStatus(member.Status),
                    NormalizeAvatarUrl(member.AvatarUrl),
                    channelNames.TryGetValue((member.ChannelId ?? string.Empty).Trim(), out var channelName)
                        ? channelName
                        : string.Empty))
                .OrderBy(member => GetStatusRank(member.Status))
                .ThenBy(member => member.Username, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var widgetInvite = TryNormalizeInviteUrl(widget.InstantInvite);
            var inviteUrl = string.IsNullOrWhiteSpace(widgetInvite) ? configuredInviteUrl : widgetInvite;
            var onlineCount = Math.Max(widget.PresenceCount, members.Length);
            var serverName = string.IsNullOrWhiteSpace(widget.Name) ? "Discord Server" : widget.Name.Trim();
            var refreshedAtUtc = DateTimeOffset.UtcNow;

            return new DiscordSnapshot(
                serverId,
                configuredInviteUrl,
                Configured: true,
                Connected: true,
                serverName,
                inviteUrl,
                onlineCount,
                members,
                channels,
                refreshedAtUtc,
                $"{onlineCount} member{(onlineCount == 1 ? string.Empty : "s")} online on {serverName}.",
                ErrorMessage: null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BuildErrorSnapshot(configuration, "Discord did not respond before the request timed out.");
        }
        catch (HttpRequestException exception)
        {
            return BuildErrorSnapshot(configuration, $"Discord could not be reached: {exception.Message}");
        }
        catch (JsonException)
        {
            return BuildErrorSnapshot(configuration, "Discord returned a server widget response that could not be read.");
        }
    }

    private async Task<DiscordSnapshot> FetchSocialSnapshotAsync(
        DiscordConfiguration configuration,
        string applicationId,
        CancellationToken cancellationToken)
    {
        if (_socialClient is null)
        {
            throw new InvalidOperationException("Discord Social SDK is not available.");
        }

        var hasStoredToken = !string.IsNullOrWhiteSpace(configuration.AccessToken);
        var needsPermissionUpgrade = hasStoredToken &&
            configuration.TokenProvider.Equals("social-sdk", StringComparison.Ordinal) &&
            !HasScope(configuration.TokenScopes, "guilds");
        if (hasStoredToken &&
            (!configuration.TokenProvider.Equals("social-sdk", StringComparison.Ordinal) || needsPermissionUpgrade))
        {
            ClearRpcSession(configuration);
        }

        if (string.IsNullOrWhiteSpace(configuration.AccessToken))
        {
            return BuildSocialDisconnectedSnapshot(
                configuration,
                applicationId,
                needsPermissionUpgrade
                    ? "Discord needs one permission update to add your server list. Connect again and approve the new server-list permission."
                    : "Connect Discord once to see online friends and your servers.");
        }

        try
        {
            var session = await _socialClient.ResumeAsync(
                applicationId,
                configuration.AccessToken,
                configuration.RefreshToken,
                configuration.TokenExpiresAtUtc,
                cancellationToken);
            if (!string.Equals(configuration.AccessToken, session.Token.AccessToken, StringComparison.Ordinal) ||
                !string.Equals(configuration.RefreshToken, session.Token.RefreshToken, StringComparison.Ordinal) ||
                configuration.TokenExpiresAtUtc != session.Token.ExpiresAtUtc)
            {
                configuration.AccessToken = session.Token.AccessToken;
                configuration.RefreshToken = session.Token.RefreshToken;
                configuration.TokenExpiresAtUtc = session.Token.ExpiresAtUtc;
                configuration.TokenScopes = session.Token.Scopes;
                _settingsStore.Save(configuration);
            }

            var socialFriends = await _socialClient.GetFriendsAsync(cancellationToken);
            var friends = socialFriends
                .Select(friend => new DiscordFriendState(
                    friend.Id,
                    friend.Username,
                    friend.DisplayName,
                    NormalizeAvatarUrl(friend.AvatarUrl),
                    friend.Status))
                .ToArray();
            IReadOnlyList<DiscordGuildState> guilds = [];
            var guildsErrorMessage = string.Empty;
            try
            {
                guilds = await FetchCurrentUserGuildsAsync(session.Token.AccessToken, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                guildsErrorMessage = $"Discord servers could not be loaded: {exception.Message}";
            }
            catch (JsonException)
            {
                guildsErrorMessage = "Discord returned a server list that could not be read.";
            }

            var selectedGuild = guilds.FirstOrDefault(guild =>
                string.Equals(guild.Id, configuration.SelectedGuildId, StringComparison.Ordinal));
            if (selectedGuild is null && !string.IsNullOrWhiteSpace(configuration.SelectedGuildId))
            {
                configuration.SelectedGuildId = string.Empty;
                _settingsStore.Save(configuration);
            }

            var onlineCount = friends.Count(friend => !friend.Status.Equals("offline", StringComparison.Ordinal));
            var account = new DiscordAccountState(
                session.User.Id,
                session.User.Username,
                session.User.DisplayName,
                NormalizeAvatarUrl(session.User.AvatarUrl));
            var status = onlineCount == 1
                ? $"Connected as {account.DisplayName}. 1 friend is online."
                : $"Connected as {account.DisplayName}. {onlineCount} friends are online.";

            return new DiscordSnapshot(
                ServerId: configuration.ServerId,
                ConfiguredInviteUrl: configuration.InviteUrl,
                Configured: true,
                Connected: true,
                ServerName: string.Empty,
                InviteUrl: TryNormalizeInviteUrl(configuration.InviteUrl),
                OnlineCount: onlineCount,
                Members: [],
                Channels: [],
                RefreshedAtUtc: DateTimeOffset.UtcNow,
                StatusText: status,
                ErrorMessage: null,
                ApplicationConfigured: true,
                DiscordRunning: IsDiscordDesktopRunning(),
                Authorized: true,
                Account: account,
                Guilds: guilds,
                SelectedGuildId: selectedGuild?.Id ?? string.Empty,
                SelectedGuildName: selectedGuild?.Name ?? string.Empty,
                VoiceChannels: [],
                ConnectionMode: "social-sdk",
                ApplicationId: applicationId,
                Friends: friends,
                SdkVersion: "1.9.17380",
                GuildsErrorMessage: guildsErrorMessage);
        }
        catch (DiscordSocialSdkException exception)
        {
            if (exception.Message.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
                exception.Message.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
                exception.Message.Contains("authorization", StringComparison.OrdinalIgnoreCase))
            {
                ClearRpcSession(configuration);
            }

            return BuildSocialErrorSnapshot(configuration, applicationId, exception.Message);
        }
    }

    private static DiscordSnapshot BuildSocialDisconnectedSnapshot(
        DiscordConfiguration configuration,
        string applicationId,
        string statusText)
    {
        return new DiscordSnapshot(
            ServerId: configuration.ServerId,
            ConfiguredInviteUrl: configuration.InviteUrl,
            Configured: true,
            Connected: false,
            ServerName: string.Empty,
            InviteUrl: TryNormalizeInviteUrl(configuration.InviteUrl),
            OnlineCount: 0,
            Members: [],
            Channels: [],
            RefreshedAtUtc: null,
            StatusText: statusText,
            ErrorMessage: null,
            ApplicationConfigured: true,
            DiscordRunning: IsDiscordDesktopRunning(),
            Authorized: false,
            Account: null,
            Guilds: [],
            SelectedGuildId: string.Empty,
            SelectedGuildName: string.Empty,
            VoiceChannels: [],
            ConnectionMode: "social-sdk",
            ApplicationId: applicationId,
            Friends: [],
            SdkVersion: "1.9.17380");
    }

    private async Task<IReadOnlyList<DiscordGuildState>> FetchCurrentUserGuildsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://discord.com/api/v10/users/@me/guilds?with_counts=true&limit=200");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "ToolsForSteam/0.4.1-beta.1 (+https://github.com/toonymak1993/tools-for-steam)");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Discord returned {(int)response.StatusCode} ({response.ReasonPhrase})",
                inner: null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var guilds = await JsonSerializer.DeserializeAsync<IReadOnlyList<DiscordUserGuildResponse>>(
            stream,
            JsonOptions,
            cancellationToken) ?? [];
        return guilds
            .Where(guild => !string.IsNullOrWhiteSpace(guild.Id) && !string.IsNullOrWhiteSpace(guild.Name))
            .Select(guild => new DiscordGuildState(
                guild.Id!,
                guild.Name!,
                BuildGuildIconUrl(guild.Id!, guild.Icon),
                Math.Max(0, guild.ApproximatePresenceCount),
                Math.Max(0, guild.ApproximateMemberCount)))
            .OrderBy(guild => guild.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DiscordSnapshot BuildSocialErrorSnapshot(
        DiscordConfiguration configuration,
        string applicationId,
        string message)
    {
        var disconnected = BuildSocialDisconnectedSnapshot(configuration, applicationId, message);
        return disconnected with
        {
            Authorized = !string.IsNullOrWhiteSpace(configuration.AccessToken),
            ErrorMessage = message
        };
    }

    private async Task<DiscordSnapshot> FetchRpcSnapshotAsync(
        DiscordConfiguration configuration,
        string applicationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.AccessToken))
        {
            return BuildRpcDisconnectedSnapshot(
                configuration,
                applicationId,
                IsDiscordDesktopRunning(),
                "Connect your Discord account to browse servers and voice channels.");
        }

        try
        {
            if (configuration.TokenExpiresAtUtc is { } expiresAt &&
                expiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                if (string.IsNullOrWhiteSpace(configuration.RefreshToken))
                {
                    ClearRpcSession(configuration);
                    return BuildRpcDisconnectedSnapshot(
                        configuration,
                        applicationId,
                        IsDiscordDesktopRunning(),
                        "Your Discord session expired. Connect Discord again.");
                }

                var refreshedToken = await _rpcClient.RefreshTokenAsync(
                    applicationId,
                    configuration.RefreshToken,
                    cancellationToken);
                configuration.AccessToken = refreshedToken.AccessToken;
                configuration.RefreshToken = refreshedToken.RefreshToken;
                configuration.TokenExpiresAtUtc = refreshedToken.ExpiresAtUtc;
                _settingsStore.Save(configuration);
            }

            var authentication = await _rpcClient.AuthenticateAsync(
                applicationId,
                configuration.AccessToken,
                cancellationToken);
            var guilds = await _rpcClient.GetGuildsAsync(cancellationToken);
            var selectedGuild = guilds.FirstOrDefault(guild =>
                string.Equals(guild.Id, configuration.SelectedGuildId, StringComparison.Ordinal));
            if (selectedGuild is null && !string.IsNullOrWhiteSpace(configuration.SelectedGuildId))
            {
                configuration.SelectedGuildId = string.Empty;
                _settingsStore.Save(configuration);
            }

            var voiceChannels = selectedGuild is null
                ? []
                : await LoadVoiceChannelsAsync(selectedGuild.Id, cancellationToken);
            var account = new DiscordAccountState(
                authentication.User.Id,
                authentication.User.Username,
                authentication.User.DisplayName,
                BuildAvatarUrl(authentication.User.Id, authentication.User.AvatarHash));
            var refreshedAt = DateTimeOffset.UtcNow;
            var visibleParticipants = voiceChannels.Sum(channel => channel.Participants.Count);
            var status = selectedGuild is null
                ? $"Connected as {account.DisplayName}. Choose one of {guilds.Count} servers."
                : $"{voiceChannels.Count} voice channels and {visibleParticipants} connected participants on {selectedGuild.Name}.";

            return new DiscordSnapshot(
                ServerId: configuration.ServerId,
                ConfiguredInviteUrl: configuration.InviteUrl,
                Configured: true,
                Connected: true,
                ServerName: selectedGuild?.Name ?? string.Empty,
                InviteUrl: TryNormalizeInviteUrl(configuration.InviteUrl),
                OnlineCount: visibleParticipants,
                Members: [],
                Channels: voiceChannels.Select(channel => new DiscordChannelState(channel.Id, channel.Name)).ToArray(),
                RefreshedAtUtc: refreshedAt,
                StatusText: status,
                ErrorMessage: null,
                ApplicationConfigured: true,
                DiscordRunning: true,
                Authorized: true,
                Account: account,
                Guilds: guilds.Select(guild => new DiscordGuildState(guild.Id, guild.Name, NormalizeAvatarUrl(guild.IconUrl))).ToArray(),
                SelectedGuildId: selectedGuild?.Id ?? string.Empty,
                SelectedGuildName: selectedGuild?.Name ?? string.Empty,
                VoiceChannels: voiceChannels,
                ConnectionMode: "rpc",
                ApplicationId: applicationId);
        }
        catch (DiscordNotRunningException exception)
        {
            return BuildRpcErrorSnapshot(
                configuration,
                applicationId,
                discordRunning: false,
                authorized: true,
                exception.Message);
        }
        catch (DiscordRpcException exception)
        {
            if (exception.Code is 4000 or 4001 or 4003 or 5000)
            {
                ClearRpcSession(configuration);
            }

            return BuildRpcErrorSnapshot(
                configuration,
                applicationId,
                IsDiscordDesktopRunning(),
                authorized: !string.IsNullOrWhiteSpace(configuration.AccessToken),
                exception.Message);
        }
    }

    private async Task<IReadOnlyList<DiscordVoiceChannelState>> LoadVoiceChannelsAsync(
        string guildId,
        CancellationToken cancellationToken)
    {
        var channels = await _rpcClient.GetGuildChannelsAsync(guildId, cancellationToken);
        var selectedVoiceChannelId = await _rpcClient.GetSelectedVoiceChannelIdAsync(cancellationToken);
        var voiceChannels = new List<DiscordVoiceChannelState>();
        foreach (var channel in channels.Where(channel => channel.Type is 2 or 13).Take(64))
        {
            var details = await _rpcClient.GetChannelAsync(channel.Id, cancellationToken);
            var participants = details.VoiceStates
                .Select(voiceState => new DiscordVoiceParticipantState(
                    voiceState.User.Id,
                    voiceState.User.Username,
                    string.IsNullOrWhiteSpace(voiceState.Nickname)
                        ? voiceState.User.DisplayName
                        : voiceState.Nickname,
                    BuildAvatarUrl(voiceState.User.Id, voiceState.User.AvatarHash),
                    voiceState.Muted,
                    voiceState.Deafened))
                .OrderBy(participant => participant.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            voiceChannels.Add(new DiscordVoiceChannelState(
                channel.Id,
                channel.Name,
                channel.Position,
                Connected: string.Equals(channel.Id, selectedVoiceChannelId, StringComparison.Ordinal),
                participants));
        }

        return voiceChannels;
    }

    private static DiscordSnapshot BuildRpcDisconnectedSnapshot(
        DiscordConfiguration configuration,
        string applicationId,
        bool discordRunning,
        string statusText)
    {
        return new DiscordSnapshot(
            ServerId: configuration.ServerId,
            ConfiguredInviteUrl: configuration.InviteUrl,
            Configured: true,
            Connected: false,
            ServerName: string.Empty,
            InviteUrl: TryNormalizeInviteUrl(configuration.InviteUrl),
            OnlineCount: 0,
            Members: [],
            Channels: [],
            RefreshedAtUtc: null,
            StatusText: statusText,
            ErrorMessage: null,
            ApplicationConfigured: !string.IsNullOrWhiteSpace(applicationId),
            DiscordRunning: discordRunning,
            Authorized: false,
            Account: null,
            Guilds: [],
            SelectedGuildId: string.Empty,
            SelectedGuildName: string.Empty,
            VoiceChannels: [],
            ConnectionMode: "rpc",
            ApplicationId: applicationId);
    }

    private static DiscordSnapshot BuildRpcErrorSnapshot(
        DiscordConfiguration configuration,
        string applicationId,
        bool discordRunning,
        bool authorized,
        string errorMessage)
    {
        return BuildRpcDisconnectedSnapshot(
            configuration,
            applicationId,
            discordRunning,
            authorized ? "Discord is connected, but its server data is currently unavailable." : "Discord is not connected.") with
        {
            Authorized = authorized,
            ErrorMessage = errorMessage
        };
    }

    private void ClearRpcSession(DiscordConfiguration configuration)
    {
        configuration.AccessToken = string.Empty;
        configuration.RefreshToken = string.Empty;
        configuration.TokenExpiresAtUtc = null;
        configuration.SelectedGuildId = string.Empty;
        configuration.TokenProvider = string.Empty;
        configuration.TokenScopes = string.Empty;
        _settingsStore.Save(configuration);
    }

    private static string ResolveApplicationId(DiscordConfiguration configuration)
    {
        var publishedValue = typeof(DiscordService).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key.Equals("DiscordClientId", StringComparison.Ordinal))
            ?.Value;
        var published = NormalizeOptionalSnowflake(publishedValue, "Discord application ID");
        if (!string.IsNullOrWhiteSpace(published))
        {
            return published;
        }

        var environmentValue = NormalizeOptionalSnowflake(
            Environment.GetEnvironmentVariable("TOOLS_FOR_STEAM_DISCORD_CLIENT_ID"),
            "Discord application ID");
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        var configured = NormalizeOptionalSnowflake(configuration.ApplicationId, "Discord application ID");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "discord-client-id.txt");
            return File.Exists(path)
                ? NormalizeOptionalSnowflake(File.ReadAllText(path), "Discord application ID")
                : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static string NormalizeOptionalSnowflake(string? value, string label)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (normalized.Length is < 16 or > 32 ||
            !normalized.All(char.IsAsciiDigit) ||
            !ulong.TryParse(normalized, out var parsed) ||
            parsed == 0)
        {
            throw new InvalidOperationException($"The {label.ToLowerInvariant()} must be a valid numeric snowflake.");
        }

        return normalized;
    }

    private static string BuildAvatarUrl(string userId, string avatarHash)
    {
        return string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(avatarHash)
            ? string.Empty
            : $"https://cdn.discordapp.com/avatars/{userId}/{avatarHash}.png?size=128";
    }

    private static string BuildGuildIconUrl(string guildId, string? iconHash)
    {
        return string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(iconHash)
            ? string.Empty
            : $"https://cdn.discordapp.com/icons/{guildId}/{iconHash}.webp?size=128";
    }

    private static bool HasScope(string? scopes, string requiredScope)
    {
        return (scopes ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(requiredScope, StringComparer.Ordinal);
    }

    private static bool IsDiscordDesktopRunning()
    {
        return new[] { "Discord", "DiscordCanary", "DiscordPTB", "DiscordDevelopment" }
            .Any(processName =>
            {
                try
                {
                    return Process.GetProcessesByName(processName).Length > 0;
                }
                catch
                {
                    return false;
                }
            });
    }

    private static async Task BringDiscordToForegroundAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var processName in new[] { "Discord", "DiscordCanary", "DiscordPTB", "DiscordDevelopment" })
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        try
                        {
                            process.Refresh();
                            var handle = process.MainWindowHandle;
                            if (handle == IntPtr.Zero)
                            {
                                continue;
                            }

                            ShowWindowAsync(handle, ShowWindowRestore);
                            SetForegroundWindow(handle);
                            return;
                        }
                        catch (InvalidOperationException)
                        {
                        }
                    }
                }
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    private const int ShowWindowRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    private void InvalidateCache()
    {
        _cachedSnapshot = null;
        _cachedAtUtc = DateTimeOffset.MinValue;
    }

    private static DiscordSnapshot BuildUnconfiguredSnapshot()
    {
        return new DiscordSnapshot(
            ServerId: string.Empty,
            ConfiguredInviteUrl: string.Empty,
            Configured: false,
            Connected: false,
            ServerName: string.Empty,
            InviteUrl: string.Empty,
            OnlineCount: 0,
            Members: [],
            Channels: [],
            RefreshedAtUtc: null,
            StatusText: "Add a Discord server ID to connect the server widget.",
            ErrorMessage: null);
    }

    private static DiscordSnapshot BuildErrorSnapshot(
        DiscordConfiguration configuration,
        string errorMessage)
    {
        return new DiscordSnapshot(
            ServerId: (configuration.ServerId ?? string.Empty).Trim(),
            ConfiguredInviteUrl: (configuration.InviteUrl ?? string.Empty).Trim(),
            Configured: !string.IsNullOrWhiteSpace(configuration.ServerId),
            Connected: false,
            ServerName: string.Empty,
            InviteUrl: TryNormalizeInviteUrl(configuration.InviteUrl),
            OnlineCount: 0,
            Members: [],
            Channels: [],
            RefreshedAtUtc: null,
            StatusText: "Discord server status is unavailable.",
            ErrorMessage: errorMessage);
    }

    private static string TryNormalizeInviteUrl(string? value)
    {
        try
        {
            return NormalizeInviteUrl(value);
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static async Task<DiscordApiErrorResponse?> TryReadDiscordErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<DiscordApiErrorResponse>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSafeInviteCode(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Length <= 128 &&
               value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static string NormalizeAvatarUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               AvatarHosts.Contains(uri.Host)
            ? uri.ToString()
            : string.Empty;
    }

    private static string NormalizeStatus(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "idle" => "idle",
            "dnd" => "dnd",
            _ => "online"
        };
    }

    private static int GetStatusRank(string status)
    {
        return status switch
        {
            "online" => 0,
            "idle" => 1,
            "dnd" => 2,
            _ => 3
        };
    }

    private sealed class DiscordWidgetResponse
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        [JsonPropertyName("instant_invite")]
        public string? InstantInvite { get; init; }

        [JsonPropertyName("presence_count")]
        public int PresenceCount { get; init; }

        public IReadOnlyList<DiscordWidgetMember> Members { get; init; } = [];

        public IReadOnlyList<DiscordWidgetChannel> Channels { get; init; } = [];
    }

    private sealed class DiscordApiErrorResponse
    {
        public int Code { get; init; }

        public string? Message { get; init; }
    }

    private sealed class DiscordUserGuildResponse
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public string? Icon { get; init; }

        [JsonPropertyName("approximate_presence_count")]
        public int ApproximatePresenceCount { get; init; }

        [JsonPropertyName("approximate_member_count")]
        public int ApproximateMemberCount { get; init; }
    }

    private sealed class DiscordWidgetMember
    {
        public string? Id { get; init; }

        public string? Username { get; init; }

        public string? Status { get; init; }

        [JsonPropertyName("avatar_url")]
        public string? AvatarUrl { get; init; }

        [JsonPropertyName("channel_id")]
        public string? ChannelId { get; init; }
    }

    private sealed class DiscordWidgetChannel
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public int Position { get; init; }
    }
}
