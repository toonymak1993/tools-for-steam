namespace SteamLoader.App.Infrastructure.Artwork;

/// <summary>SteamGridDB filters of the artwork page, remembered per artwork type by the frontend.</summary>
public sealed record ArtworkAssetFilter(
    IReadOnlyList<string>? Styles = null,
    string? Animation = null,
    bool AllowNsfw = false,
    bool AllowHumor = false,
    IReadOnlyList<string>? Dimensions = null);

public sealed record ArtworkPickerAsset(
    string Id,
    string Source,
    string Url,
    string ThumbnailUrl,
    int? Width,
    int? Height,
    string Mime,
    string Style,
    bool IsAnimated,
    string? Author,
    int Score,
    bool Nsfw,
    bool Humor,
    string? Label);

public sealed record ArtworkPickerGame(
    long AppId,
    string Title,
    bool IsShortcut,
    uint SteamAppId,
    int SteamGridDbGameId,
    string? SteamGridDbGameName,
    bool SteamGridDbVerified,
    string SteamGridDbMatch,
    bool SteamGridDbKeyConfigured,
    IReadOnlyDictionary<string, IReadOnlyList<ArtworkPickerAsset>> Official);
