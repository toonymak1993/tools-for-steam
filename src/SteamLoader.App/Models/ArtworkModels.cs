namespace SteamLoader.App.Models;

public sealed record ArtworkGameSearchResult(
    int Id,
    string Name,
    bool Verified);

public sealed record ArtworkAssetResult(
    string Id,
    string Url,
    string ThumbnailUrl,
    int? Width,
    int? Height,
    string Mime,
    string Style);

public sealed record ArtworkApplyResult(
    bool Success,
    string Message,
    long AppId,
    string AssetType,
    int SteamAssetType,
    string Extension,
    string Mime,
    string Base64Data,
    string? WrittenPath);

public sealed record ArtworkOpenRequest(
    long Nonce,
    long AppId,
    string Title);

public sealed record RequestArtworkOpenRequest(
    long AppId,
    string Title);

public sealed record ArtworkSettingsState(
    bool ContextMenuEnabled,
    bool SteamGridDbApiKeyConfigured,
    string SteamGridDbApiKeyPreview,
    bool PreferVerifiedMatches,
    int ResultLimit,
    SteamPathState SteamPath);

public sealed record ArtworkSnapshot(
    ArtworkSettingsState Settings,
    string StatusText);
