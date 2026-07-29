using System.Net.Http;
using System.Security.Cryptography;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Owns the pinned compatibility bridge required by Rockstar games purchased
/// through Epic. Rockstar checks for an EpicGamesLauncher-named parent process,
/// while Legendary remains responsible for generating the authenticated launch.
/// </summary>
internal static class ManagedEpicLauncherBridge
{
    internal const string Version = "v0.4";
    internal const string DownloadUrl =
        "https://github.com/Etaash-mathamsetty/heroic-epic-integration/releases/download/v0.4/EpicGamesLauncher.exe";
    internal const string Sha256 =
        "1feb21e21e19cbb34e881791c8f2557769f59231e46850915d49a6d0c1ce4583";

    private static readonly object InstallGate = new();
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    public static string ToolDirectory =>
        Path.Combine(AppContext.BaseDirectory, "data", "omnilibrary", "helpers");

    public static string ToolPath =>
        Path.Combine(ToolDirectory, "EpicGamesLauncher.exe");

    public static string EnsureInstalled()
    {
        lock (InstallGate)
        {
            if (File.Exists(ToolPath) && HasExpectedHash(ToolPath))
            {
                return ToolPath;
            }

            Directory.CreateDirectory(ToolDirectory);
            var temporaryPath = Path.Combine(
                ToolDirectory,
                $"epic-launcher-bridge-{Version}-{Guid.NewGuid():N}.tmp");

            try
            {
                using var response = HttpClient.GetAsync(
                        DownloadUrl,
                        HttpCompletionOption.ResponseHeadersRead)
                    .GetAwaiter()
                    .GetResult();
                response.EnsureSuccessStatusCode();

                using (var source = response.Content.ReadAsStream())
                using (var destination = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    source.CopyTo(destination);
                    destination.Flush(flushToDisk: true);
                }

                if (!HasExpectedHash(temporaryPath))
                {
                    throw new InvalidOperationException(
                        "The downloaded Rockstar compatibility component failed its integrity check.");
                }

                File.Move(temporaryPath, ToolPath, overwrite: true);
                return ToolPath;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                }
            }
        }
    }

    internal static bool HasExpectedHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            return actualHash.Equals(Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
