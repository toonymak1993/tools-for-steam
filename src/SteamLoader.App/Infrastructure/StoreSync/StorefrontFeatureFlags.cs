using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal static class StorefrontFeatureFlags
{
    // OmniLibrary is a settings surface plus an Xbox-only native Library bridge.
    // Its dedicated sync stays independent from normal Store Sync status and analysis.
    public static bool Enabled => true;

    public static bool SteamShortcutSyncEnabled => false;

    public static bool AutomaticRefreshEnabled => false;

    public static bool IsDisabledRequestPath(string? path)
    {
        if (Enabled || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return path.StartsWith("/api/unifystore", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/api/store-sync/unifysteam", StringComparison.OrdinalIgnoreCase);
    }

    public static UnifySteamSnapshot BuildDisabledSnapshot()
    {
        return new UnifySteamSnapshot(
            "Disabled",
            "OmniLibrary is disabled in this build.",
            null,
            []);
    }
}
