namespace SteamLoader.App.Models;

public sealed record UpdateCheckSnapshot(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    bool CanInstall,
    string Message,
    string? ReleaseUrl,
    string? AssetName,
    DateTimeOffset? PublishedAtUtc);
