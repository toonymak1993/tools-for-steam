using System.Net;
using System.Text;
using System.Buffers.Binary;
using SteamLoader.App.Infrastructure.Discord;
using SteamLoader.App.Models;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class DiscordServiceTests
{
    [Fact]
    public void NormalizeInviteUrl_AcceptsCodesAndDiscordInviteUrls()
    {
        Assert.Equal("https://discord.gg/hello_world", DiscordService.NormalizeInviteUrl("hello_world"));
        Assert.Equal(
            "https://discord.gg/AbC-123",
            DiscordService.NormalizeInviteUrl("https://discord.com/invite/AbC-123?utm_source=test"));
    }

    [Theory]
    [InlineData("http://discord.gg/example")]
    [InlineData("https://example.com/invite/test")]
    [InlineData("https://discord.com/channels/123/456")]
    [InlineData("javascript:alert(1)")]
    public void NormalizeInviteUrl_RejectsUnsafeValues(string value)
    {
        Assert.Throws<InvalidOperationException>(() => DiscordService.NormalizeInviteUrl(value));
    }

    [Fact]
    public async Task GetSnapshotAsync_MapsWidgetInviteMembersAndVoiceChannels()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(root, "discord.json");
            var settingsStore = new DiscordSettingsStore(settingsPath);
            settingsStore.Save(new DiscordConfiguration
            {
                ServerId = "123456789012345678",
                InviteUrl = "fallback-code"
            });
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.OK,
                """
                {
                  "id": "123456789012345678",
                  "name": "Living Room Players",
                  "instant_invite": "https://discord.gg/widget-code",
                  "presence_count": 3,
                  "channels": [
                    { "id": "voice-1", "name": "Couch Co-op", "position": 1 }
                  ],
                  "members": [
                    {
                      "id": "2",
                      "username": "Idle Player",
                      "status": "idle",
                      "avatar_url": "https://cdn.discordapp.com/widget-avatars/idle.png"
                    },
                    {
                      "id": "1",
                      "username": "Online Player",
                      "status": "online",
                      "avatar_url": "https://cdn.discordapp.com/widget-avatars/online.png",
                      "channel_id": "voice-1"
                    }
                  ]
                }
                """);
            using var httpClient = new HttpClient(handler);
            var service = new DiscordService(httpClient, settingsStore);

            var snapshot = await service.GetWidgetFallbackSnapshotAsync(CancellationToken.None);

            Assert.True(snapshot.Configured);
            Assert.True(snapshot.Connected);
            Assert.Equal("Living Room Players", snapshot.ServerName);
            Assert.Equal("https://discord.gg/widget-code", snapshot.InviteUrl);
            Assert.Equal(3, snapshot.OnlineCount);
            Assert.Equal("Online Player", snapshot.Members[0].Username);
            Assert.Equal("Couch Co-op", snapshot.Members[0].VoiceChannelName);
            Assert.Equal("Idle Player", snapshot.Members[1].Username);
            Assert.Equal("https://discord.com/api/v10/guilds/123456789012345678/widget.json", handler.RequestUri?.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_ExplainsWhenWidgetIsNotEnabled()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settingsStore = new DiscordSettingsStore(Path.Combine(root, "discord.json"));
            settingsStore.Save(new DiscordConfiguration
            {
                ServerId = "123456789012345678"
            });
            using var httpClient = new HttpClient(new StubHttpMessageHandler(
                HttpStatusCode.Forbidden,
                "{\"message\":\"Widget Disabled\",\"code\":50004}"));
            var service = new DiscordService(httpClient, settingsStore);

            var snapshot = await service.GetWidgetFallbackSnapshotAsync(CancellationToken.None);

            Assert.True(snapshot.Configured);
            Assert.False(snapshot.Connected);
            Assert.Contains("Engagement (Beteiligung)", snapshot.ErrorMessage);
            Assert.DoesNotContain("403", snapshot.ErrorMessage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_MapsRpcServersVoiceChannelsAndParticipants()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settingsStore = new DiscordSettingsStore(Path.Combine(root, "discord.json"));
            settingsStore.Save(new DiscordConfiguration
            {
                ApplicationId = "123456789012345678",
                AccessToken = "access-token",
                RefreshToken = "refresh-token",
                TokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
                SelectedGuildId = "223456789012345678"
            });
            var rpcClient = new StubDiscordRpcClient();
            using var httpClient = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, "{}"));
            await using var service = new DiscordService(httpClient, settingsStore, rpcClient);

            var snapshot = await service.GetSnapshotAsync(forceRefresh: true, CancellationToken.None);

            Assert.Equal("rpc", snapshot.ConnectionMode);
            Assert.True(snapshot.ApplicationConfigured);
            Assert.True(snapshot.Authorized);
            Assert.Equal("Living Room Players", snapshot.SelectedGuildName);
            var channel = Assert.Single(snapshot.VoiceChannels!);
            Assert.Equal("Couch Co-op", channel.Name);
            Assert.True(channel.Connected);
            var participant = Assert.Single(channel.Participants);
            Assert.Equal("Player Nickname", participant.DisplayName);
            Assert.True(participant.Muted);
            Assert.Contains("cdn.discordapp.com/avatars", participant.AvatarUrl);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_MapsSocialFriendsAndGuildPresenceCounts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settingsStore = new DiscordSettingsStore(Path.Combine(root, "discord.json"));
            settingsStore.Save(new DiscordConfiguration
            {
                ApplicationId = "123456789012345678",
                AccessToken = "social-access-token",
                RefreshToken = "social-refresh-token",
                TokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(2),
                TokenProvider = "social-sdk",
                TokenScopes = DiscordSocialSdkClient.RequiredScopes
            });
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.OK,
                """
                [
                  {
                    "id": "223456789012345678",
                    "name": "Living Room Players",
                    "icon": "guild-icon",
                    "approximate_presence_count": 14,
                    "approximate_member_count": 42
                  }
                ]
                """);
            using var httpClient = new HttpClient(handler);
            await using var service = new DiscordService(
                httpClient,
                settingsStore,
                new StubDiscordRpcClient(),
                new StubDiscordSocialSdkClient());

            var snapshot = await service.GetSnapshotAsync(forceRefresh: true, CancellationToken.None);

            Assert.Equal("social-sdk", snapshot.ConnectionMode);
            Assert.True(snapshot.Authorized);
            Assert.Equal(2, snapshot.Friends!.Count);
            Assert.Equal(1, snapshot.OnlineCount);
            var guild = Assert.Single(snapshot.Guilds!);
            Assert.Equal("Living Room Players", guild.Name);
            Assert.Equal(14, guild.OnlineCount);
            Assert.Equal(42, guild.MemberCount);
            Assert.Equal(
                "https://discord.com/api/v10/users/@me/guilds?with_counts=true&limit=200",
                handler.RequestUri?.ToString());
            Assert.Equal("Bearer", handler.AuthorizationScheme);
            Assert.Equal("social-access-token", handler.AuthorizationParameter);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GuildFavorites_ArePersistedDecoratedAndSortedFirst()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settingsStore = new DiscordSettingsStore(Path.Combine(root, "discord.json"));
            settingsStore.Save(new DiscordConfiguration
            {
                ApplicationId = "123456789012345678",
                AccessToken = "social-access-token",
                RefreshToken = "social-refresh-token",
                TokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(2),
                TokenProvider = "social-sdk",
                TokenScopes = DiscordSocialSdkClient.RequiredScopes
            });
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.OK,
                """
                [
                  {
                    "id": "223456789012345678",
                    "name": "Alpha Server",
                    "approximate_presence_count": 4,
                    "approximate_member_count": 20
                  },
                  {
                    "id": "323456789012345678",
                    "name": "Zulu Server",
                    "approximate_presence_count": 8,
                    "approximate_member_count": 30
                  }
                ]
                """);
            using var httpClient = new HttpClient(handler);
            await using var service = new DiscordService(
                httpClient,
                settingsStore,
                new StubDiscordRpcClient(),
                new StubDiscordSocialSdkClient());

            var snapshot = await service.SetGuildFavoriteAsync(
                "323456789012345678",
                favorite: true,
                CancellationToken.None);

            Assert.Equal("Zulu Server", snapshot.Guilds![0].Name);
            Assert.True(snapshot.Guilds[0].IsFavorite);
            Assert.False(snapshot.Guilds[1].IsFavorite);
            Assert.Contains("323456789012345678", settingsStore.Load().FavoriteGuildIds);

            snapshot = await service.SetFriendOnlineNotificationsAsync(
                enabled: true,
                CancellationToken.None);

            Assert.True(snapshot.FriendOnlineNotificationsEnabled);
            Assert.True(settingsStore.Load().FriendOnlineNotificationsEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FriendPresenceTracker_NotifiesOnlyAfterOfflineToOnlineTransition()
    {
        var tracker = new DiscordFriendPresenceTracker();
        var offline = new DiscordFriendState(
            "723456789012345678",
            "player",
            "Player",
            string.Empty,
            "offline");
        var online = offline with { Status = "online" };

        Assert.Empty(tracker.Observe([offline], enabled: true));
        Assert.Single(tracker.Observe([online], enabled: true));
        Assert.Empty(tracker.Observe([online], enabled: true));
        Assert.Empty(tracker.Observe([online], enabled: false));
        Assert.Empty(tracker.Observe([online], enabled: true));
    }

    [Fact]
    public void SettingsStore_EncryptsDiscordTokensForCurrentWindowsUser()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "discord.json");
            var store = new DiscordSettingsStore(path);
            store.Save(new DiscordConfiguration
            {
                ApplicationId = "123456789012345678",
                AccessToken = "private-access-token",
                RefreshToken = "private-refresh-token",
                FavoriteGuildIds = ["223456789012345678"],
                FriendOnlineNotificationsEnabled = true
            });

            var persisted = File.ReadAllText(path);
            Assert.DoesNotContain("private-access-token", persisted);
            Assert.DoesNotContain("private-refresh-token", persisted);
            Assert.Equal("private-access-token", store.Load().AccessToken);
            Assert.Equal("private-refresh-token", store.Load().RefreshToken);
            Assert.Contains("223456789012345678", store.Load().FavoriteGuildIds);
            Assert.True(store.Load().FriendOnlineNotificationsEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RpcFrame_UsesDiscordLittleEndianHeaderAndUtf8Payload()
    {
        var frame = DiscordRpcClient.CreateFrame(1, "{\"cmd\":\"GET_GUILDS\"}");

        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(0, 4)));
        Assert.Equal(frame.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4, 4)));
        Assert.Equal("{\"cmd\":\"GET_GUILDS\"}", Encoding.UTF8.GetString(frame.AsSpan(8)));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tfs-discord-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public StubHttpMessageHandler(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        public Uri? RequestUri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubDiscordRpcClient : IDiscordRpcClient
    {
        public Task<DiscordRpcAuthentication> AuthorizeAsync(string applicationId, CancellationToken cancellationToken)
        {
            return Task.FromResult(CreateAuthentication());
        }

        public Task<DiscordRpcAuthentication> AuthenticateAsync(
            string applicationId,
            string accessToken,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(CreateAuthentication());
        }

        public Task<DiscordRpcToken> RefreshTokenAsync(
            string applicationId,
            string refreshToken,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new DiscordRpcToken(
                "refreshed-access-token",
                "refreshed-refresh-token",
                DateTimeOffset.UtcNow.AddHours(1)));
        }

        public Task<IReadOnlyList<DiscordRpcGuild>> GetGuildsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<DiscordRpcGuild>>([
                new DiscordRpcGuild("223456789012345678", "Living Room Players", string.Empty)
            ]);
        }

        public Task<IReadOnlyList<DiscordRpcChannel>> GetGuildChannelsAsync(
            string guildId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<DiscordRpcChannel>>([
                new DiscordRpcChannel("323456789012345678", "Couch Co-op", 2, 1),
                new DiscordRpcChannel("423456789012345678", "general", 0, 0)
            ]);
        }

        public Task<DiscordRpcChannelDetails> GetChannelAsync(
            string channelId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new DiscordRpcChannelDetails(
                channelId,
                "Couch Co-op",
                2,
                [
                    new DiscordRpcVoiceState(
                        new DiscordRpcUser("523456789012345678", "player", "Player", "avatar-hash"),
                        "Player Nickname",
                        Muted: true,
                        Deafened: false)
                ]));
        }

        public Task SelectVoiceChannelAsync(string channelId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<string> GetSelectedVoiceChannelIdAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult("323456789012345678");
        }

        public Task DisconnectAsync()
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        private static DiscordRpcAuthentication CreateAuthentication()
        {
            return new DiscordRpcAuthentication(
                new DiscordRpcToken("access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1)),
                new DiscordRpcUser("623456789012345678", "owner", "Owner", "owner-avatar"));
        }
    }

    private sealed class StubDiscordSocialSdkClient : IDiscordSocialSdkClient
    {
        private static readonly DiscordSocialSession Session = new(
            new DiscordSocialToken(
                "social-access-token",
                "social-refresh-token",
                DateTimeOffset.UtcNow.AddHours(2),
                DiscordSocialSdkClient.RequiredScopes),
            new DiscordSocialUser(
                "623456789012345678",
                "owner",
                "Owner",
                "https://cdn.discordapp.com/avatars/623456789012345678/owner.webp",
                "online"));

        public Task<DiscordSocialSession> AuthorizeAsync(
            string applicationId,
            CancellationToken cancellationToken) => Task.FromResult(Session);

        public Task<DiscordSocialSession> ResumeAsync(
            string applicationId,
            string accessToken,
            string refreshToken,
            DateTimeOffset? expiresAtUtc,
            CancellationToken cancellationToken) => Task.FromResult(Session);

        public Task<IReadOnlyList<DiscordSocialFriend>> GetFriendsAsync(
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<DiscordSocialFriend>>([
                new DiscordSocialFriend(
                    "723456789012345678",
                    "online-friend",
                    "Online Friend",
                    string.Empty,
                    "online"),
                new DiscordSocialFriend(
                    "823456789012345678",
                    "offline-friend",
                    "Offline Friend",
                    string.Empty,
                    "offline")
            ]);
        }

        public Task DisconnectAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
