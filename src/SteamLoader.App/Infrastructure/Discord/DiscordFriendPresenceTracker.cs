using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.Discord;

internal sealed class DiscordFriendPresenceTracker
{
    private HashSet<string> _onlineFriendIds = new(StringComparer.Ordinal);
    private bool _hasBaseline;

    public IReadOnlyList<DiscordFriendState> Observe(
        IReadOnlyList<DiscordFriendState> friends,
        bool enabled)
    {
        if (!enabled)
        {
            Reset();
            return [];
        }

        var onlineFriends = friends
            .Where(friend =>
                !string.IsNullOrWhiteSpace(friend.Id) &&
                !string.Equals(friend.Status, "offline", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var currentOnlineFriendIds = onlineFriends
            .Select(friend => friend.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (!_hasBaseline)
        {
            _onlineFriendIds = currentOnlineFriendIds;
            _hasBaseline = true;
            return [];
        }

        var newlyOnline = onlineFriends
            .Where(friend => !_onlineFriendIds.Contains(friend.Id))
            .ToArray();
        _onlineFriendIds = currentOnlineFriendIds;
        return newlyOnline;
    }

    public void Reset()
    {
        _onlineFriendIds.Clear();
        _hasBaseline = false;
    }
}
