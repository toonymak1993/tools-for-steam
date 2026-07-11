using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal static class StorefrontFeatureFlags
{
    public static bool Enabled => false;

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
            "Storefront is disabled in this build.",
            null,
            []);
    }
}
