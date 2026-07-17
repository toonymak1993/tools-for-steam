namespace SteamLoader.App.Models;

public sealed record DiscordMemberState(
    string Id,
    string Username,
    string Status,
    string AvatarUrl,
    string VoiceChannelName);

public sealed record DiscordChannelState(
    string Id,
    string Name);

public sealed record DiscordAccountState(
    string Id,
    string Username,
    string DisplayName,
    string AvatarUrl);

public sealed record DiscordFriendState(
    string Id,
    string Username,
    string DisplayName,
    string AvatarUrl,
    string Status);

public sealed record DiscordGuildState(
    string Id,
    string Name,
    string IconUrl,
    int OnlineCount = 0,
    int MemberCount = 0);

public sealed record DiscordVoiceParticipantState(
    string Id,
    string Username,
    string DisplayName,
    string AvatarUrl,
    bool Muted,
    bool Deafened);

public sealed record DiscordVoiceChannelState(
    string Id,
    string Name,
    int Position,
    bool Connected,
    IReadOnlyList<DiscordVoiceParticipantState> Participants);

public sealed record DiscordSnapshot(
    string ServerId,
    string ConfiguredInviteUrl,
    bool Configured,
    bool Connected,
    string ServerName,
    string InviteUrl,
    int OnlineCount,
    IReadOnlyList<DiscordMemberState> Members,
    IReadOnlyList<DiscordChannelState> Channels,
    DateTimeOffset? RefreshedAtUtc,
    string StatusText,
    string? ErrorMessage,
    bool ApplicationConfigured = false,
    bool DiscordRunning = false,
    bool Authorized = false,
    DiscordAccountState? Account = null,
    IReadOnlyList<DiscordGuildState>? Guilds = null,
    string SelectedGuildId = "",
    string SelectedGuildName = "",
    IReadOnlyList<DiscordVoiceChannelState>? VoiceChannels = null,
    string ConnectionMode = "widget",
    string ApplicationId = "",
    IReadOnlyList<DiscordFriendState>? Friends = null,
    string SdkVersion = "",
    string GuildsErrorMessage = "");
