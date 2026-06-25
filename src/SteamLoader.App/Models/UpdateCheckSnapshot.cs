namespace SteamLoader.App.Models;

public sealed record UpdateCheckSnapshot(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    bool CanInstall,
    string Message,
    string? ReleaseUrl,
    string? AssetName,
    DateTimeOffset? PublishedAtUtc,
    string Channel,
    bool IsPrerelease,
    string? ReleaseName,
    DateTimeOffset CheckedAtUtc,
    bool InstallInProgress = false,
    string? InstallState = null,
    int? InstallProgressPercent = null);
