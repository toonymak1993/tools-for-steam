namespace SteamLoader.App.Models;

public sealed record StoreSyncArtworkPreviewState(
    string TitleId,
    bool Available,
    bool UsesCurrentArtwork,
    string ImageDataUri,
    string SourceLabel,
    string Message);
