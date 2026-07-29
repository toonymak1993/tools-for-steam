using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed record OmniLibraryReadiness(
    bool CurrentShortcutCatalogReady,
    bool HasEstablishedShortcutCatalog,
    bool ReadyForLibraryTab,
    bool SteamRestartRequired,
    OmniLibraryLifecycleSnapshot Lifecycle);

internal static class OmniLibraryLifecycle
{
    public static OmniLibraryReadiness Evaluate(
        UnifySteamStoreConfiguration store,
        bool authConfigured,
        bool authReady,
        int gameCount,
        bool allGamesHaveSteamAppIds,
        string currentCatalogSignature,
        string cacheError,
        DateTimeOffset? steamSessionStartedAtUtc)
    {
        var enabled = store.Enabled;
        var hasCatalog = gameCount > 0;
        var hasPreparedMarker =
            store.PreparedAtUtc.HasValue &&
            !string.IsNullOrWhiteSpace(store.PreparedCatalogSignature);
        var currentShortcutCatalogReady =
            hasCatalog &&
            allGamesHaveSteamAppIds &&
            hasPreparedMarker &&
            !string.IsNullOrWhiteSpace(currentCatalogSignature) &&
            string.Equals(
                currentCatalogSignature,
                store.PreparedCatalogSignature,
                StringComparison.OrdinalIgnoreCase);
        var hasEstablishedShortcutCatalog =
            hasPreparedMarker &&
            (store.Cache?.Games ?? []).Any(game =>
                game is not null &&
                game.SteamAppId != 0);

        // An expired login or a failed catalog probe must not remove an already
        // prepared tab. Existing shortcuts remain usable; only new deltas wait.
        var preparedForCurrentSteamSession =
            hasEstablishedShortcutCatalog &&
            steamSessionStartedAtUtc.HasValue &&
            steamSessionStartedAtUtc.Value > store.PreparedAtUtc!.Value;
        var readyForLibraryTab = enabled && preparedForCurrentSteamSession;
        var steamRestartRequired =
            enabled &&
            currentShortcutCatalogReady &&
            !preparedForCurrentSteamSession;

        var persistedFailureScope = Normalize(
            store.Lifecycle?.LastFailureScope,
            string.Empty);
        var catalogFailure =
            !string.IsNullOrWhiteSpace(cacheError) &&
            persistedFailureScope is not "shortcuts" and not "artwork" and not "authentication";
        var authentication = !enabled
            ? "disabled"
            : authReady
                ? "ready"
                : authConfigured
                    ? "required"
                    : "not-configured";
        var catalog = !enabled
            ? "idle"
            : catalogFailure
                ? hasCatalog ? "degraded" : "failed"
                : hasCatalog
                    ? "ready"
                    : authReady ? "empty" : "idle";
        var shortcuts = !enabled
            ? "idle"
            : persistedFailureScope == "shortcuts"
                ? Normalize(store.Lifecycle?.Shortcuts, hasEstablishedShortcutCatalog ? "degraded" : "failed")
            : currentShortcutCatalogReady
                ? "ready"
                : hasEstablishedShortcutCatalog
                    ? "updating"
                    : hasCatalog ? "pending" : "idle";
        var artwork = ResolveArtworkState(store);

        var (failureScope, failureDetail) = ResolveFailure(
            enabled,
            authentication,
            catalog,
            shortcuts,
            artwork,
            cacheError,
            store.PreparationDetail,
            store.Lifecycle);

        return new OmniLibraryReadiness(
            currentShortcutCatalogReady,
            hasEstablishedShortcutCatalog,
            readyForLibraryTab,
            steamRestartRequired,
            new OmniLibraryLifecycleSnapshot(
                authentication,
                catalog,
                shortcuts,
                artwork,
                failureScope,
                failureDetail));
    }

    public static void MigrateLegacyState(UnifySteamStoreConfiguration store)
    {
        store.Lifecycle ??= new OmniLibraryStoreLifecycleConfiguration();
        var lifecycle = store.Lifecycle;
        lifecycle.Authentication = Normalize(lifecycle.Authentication, "idle");
        lifecycle.Catalog = Normalize(lifecycle.Catalog, "idle");
        lifecycle.Shortcuts = Normalize(lifecycle.Shortcuts, "idle");
        lifecycle.Artwork = Normalize(lifecycle.Artwork, "idle");
        lifecycle.LastFailureScope = Normalize(lifecycle.LastFailureScope, string.Empty);
        lifecycle.LastFailureDetail = Normalize(lifecycle.LastFailureDetail, string.Empty);

        if (!store.Enabled)
        {
            lifecycle.Authentication = "disabled";
            return;
        }

        var hasGames = (store.Cache?.Games?.Count ?? 0) > 0;
        var hasShortcuts = (store.Cache?.Games ?? []).Any(game =>
            game is not null &&
            game.SteamAppId != 0);
        if (lifecycle.Catalog == "idle" && hasGames)
        {
            lifecycle.Catalog = string.IsNullOrWhiteSpace(store.Cache?.LastError)
                ? "ready"
                : "degraded";
        }
        if (lifecycle.Shortcuts == "idle" && hasShortcuts)
        {
            lifecycle.Shortcuts = !string.IsNullOrWhiteSpace(store.PreparedCatalogSignature)
                ? "ready"
                : "pending";
        }
        if (lifecycle.Artwork == "idle")
        {
            lifecycle.Artwork = ResolveArtworkState(store);
        }
        if (store.PreparationStatus.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
            !store.PreparationDetail.StartsWith(
                "Artwork preparation paused:",
                StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(lifecycle.LastFailureScope))
        {
            lifecycle.Shortcuts = hasShortcuts ? "degraded" : "failed";
            lifecycle.LastFailureScope = "shortcuts";
            lifecycle.LastFailureDetail = store.PreparationDetail;
        }
    }

    public static void SetStage(
        UnifySteamStoreConfiguration store,
        string stage,
        string status,
        string failureDetail = "")
    {
        store.Lifecycle ??= new OmniLibraryStoreLifecycleConfiguration();
        var normalizedStage = Normalize(stage, string.Empty);
        var normalizedStatus = Normalize(status, "idle");
        switch (normalizedStage)
        {
            case "authentication":
                store.Lifecycle.Authentication = normalizedStatus;
                break;
            case "catalog":
                store.Lifecycle.Catalog = normalizedStatus;
                break;
            case "shortcuts":
                store.Lifecycle.Shortcuts = normalizedStatus;
                break;
            case "artwork":
                store.Lifecycle.Artwork = normalizedStatus;
                break;
            default:
                throw new InvalidOperationException($"Unknown OmniLibrary lifecycle stage '{stage}'.");
        }

        if (!string.IsNullOrWhiteSpace(failureDetail))
        {
            store.Lifecycle.LastFailureScope = normalizedStage;
            store.Lifecycle.LastFailureDetail = failureDetail.Trim();
        }
        else if (store.Lifecycle.LastFailureScope.Equals(
                     normalizedStage,
                     StringComparison.OrdinalIgnoreCase))
        {
            store.Lifecycle.LastFailureScope = string.Empty;
            store.Lifecycle.LastFailureDetail = string.Empty;
        }
        store.Lifecycle.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private static string ResolveArtworkState(UnifySteamStoreConfiguration store)
    {
        // Once shortcuts are prepared, PreparationStatus deliberately remains
        // "prepared" while optional artwork continues. Preserve the independent
        // lifecycle stage instead of letting that public readiness marker mask it.
        var persistedArtwork = Normalize(store.Lifecycle?.Artwork, "idle");
        if (persistedArtwork is "updating" or "degraded" or "failed")
        {
            return persistedArtwork;
        }
        if (store.PreparationStatus.Equals("artwork-pending", StringComparison.OrdinalIgnoreCase))
        {
            return "degraded";
        }
        if (store.PreparationStatus.Equals("artwork", StringComparison.OrdinalIgnoreCase))
        {
            return "updating";
        }
        if (store.PreparationStatus.Equals("prepared", StringComparison.OrdinalIgnoreCase))
        {
            return "ready";
        }
        return persistedArtwork;
    }

    private static (string Scope, string Detail) ResolveFailure(
        bool enabled,
        string authentication,
        string catalog,
        string shortcuts,
        string artwork,
        string cacheError,
        string preparationDetail,
        OmniLibraryStoreLifecycleConfiguration? persisted)
    {
        if (!enabled)
        {
            return (string.Empty, string.Empty);
        }
        if (!string.IsNullOrWhiteSpace(persisted?.LastFailureScope))
        {
            return (
                persisted.LastFailureScope,
                FirstNonEmpty(
                    persisted.LastFailureDetail,
                    cacheError,
                    preparationDetail));
        }
        if (catalog is "failed" or "degraded")
        {
            return ("catalog", cacheError.Trim());
        }
        if (authentication is "required" or "not-configured")
        {
            return ("authentication", string.Empty);
        }
        if (shortcuts == "failed")
        {
            return ("shortcuts", preparationDetail.Trim());
        }
        if (artwork == "degraded")
        {
            return ("artwork", preparationDetail.Trim());
        }
        return (string.Empty, string.Empty);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ??
        string.Empty;

    private static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim().ToLowerInvariant();
}
