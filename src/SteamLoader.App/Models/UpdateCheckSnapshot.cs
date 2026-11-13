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
    string? ReleaseName,
    DateTimeOffset CheckedAtUtc,
    bool InstallInProgress = false,
    string? InstallState = null,
    int? InstallProgressPercent = null,
    // True when this TFS installs new releases on its own (installed builds
    // only; a development build from bin\ never replaces itself).
    bool AutomaticInstall = false);
