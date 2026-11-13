using System.Text.Json;
using Microsoft.Win32;
using SteamLoader.App.Infrastructure.Performance;
using SteamLoader.App.Models;

namespace SteamLoader.App.Services;

/// <summary>
/// Decides when Tools for Steam may install the newest stable release on its
/// own: only an installed build, only when no game is running, and a version
/// whose install failed (for example a declined admin prompt) is not retried
/// for a day so the user is not asked again and again.
/// </summary>
internal static class AutomaticUpdatePolicy
{
    public const string AttemptFileName = "automatic-update.json";
    public static readonly TimeSpan RetryAfterFailedAttempt = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Setup leaves its uninstaller next to an installed TFS. A development
    /// build started from the repository's bin folder has none and must never
    /// replace itself with a release.
    /// </summary>
    public static bool IsInstalledBuild(string baseDirectory)
    {
        try
        {
            return Directory.Exists(baseDirectory) &&
                Directory.EnumerateFiles(baseDirectory, "unins*.exe", SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            return false;
        }
    }

    public static bool ShouldInstall(
        UpdateCheckSnapshot snapshot,
        AutomaticUpdateAttempt? lastAttempt,
        DateTimeOffset now)
    {
        if (!snapshot.UpdateAvailable ||
            !snapshot.CanInstall ||
            snapshot.InstallInProgress ||
            string.IsNullOrWhiteSpace(snapshot.LatestVersion))
        {
            return false;
        }

        return lastAttempt is null ||
            !string.Equals(lastAttempt.Version, snapshot.LatestVersion, StringComparison.OrdinalIgnoreCase) ||
            now - lastAttempt.AttemptedAtUtc >= RetryAfterFailedAttempt;
    }

    public static AutomaticUpdateAttempt? ReadAttempt(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AutomaticUpdateAttempt>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void WriteAttempt(string path, AutomaticUpdateAttempt attempt)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(attempt, JsonOptions));
        }
        catch
        {
            // Without the marker a failed install may be retried sooner; the
            // update itself must not fail because of it.
        }
    }

    /// <summary>
    /// A game counts as running when Steam reports one or when a window that
    /// is not Steam, TFS or the Windows shell owns the foreground (covers
    /// non-Steam games and anything else the user is busy with).
    /// </summary>
    public static bool IsUserBusy()
    {
        try
        {
            using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steamKey?.GetValue("RunningAppID") is int runningAppId && runningAppId != 0)
            {
                return true;
            }
        }
        catch
        {
            return true;
        }

        return PerformanceForegroundTargetResolver.TryResolve() is not null;
    }
}

internal sealed record AutomaticUpdateAttempt(string Version, DateTimeOffset AttemptedAtUtc);
