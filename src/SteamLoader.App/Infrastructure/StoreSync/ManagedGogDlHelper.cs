using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Owns OmniLibrary's pinned heroic-gogdl helper and isolated GOG session.
/// GOG Galaxy and Heroic credentials are deliberately left untouched.
/// </summary>
internal static class ManagedGogDlHelper
{
    internal const string Version = "1.2.2";
    internal const string DownloadUrl =
        "https://github.com/Heroic-Games-Launcher/heroic-gogdl/releases/download/v1.2.2/gogdl_windows_x86_64.exe";
    internal const string Sha256 =
        "37e7cf848d35ffff92dfaeb62d7751709e0b8a0deb17dda36a013d73300e61c1";

    private static readonly object InstallGate = new();
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(3),
    };

    public static string ToolDirectory =>
        Path.Combine(AppContext.BaseDirectory, "data", "omnilibrary", "helpers");

    public static string ToolPath => Path.Combine(ToolDirectory, "gogdl.exe");

    public static string ConfigDirectory =>
        Path.Combine(AppContext.BaseDirectory, "data", "omnilibrary", "gog");

    public static string AuthPath => Path.Combine(ConfigDirectory, "auth.json");

    public static string RuntimeConfigPath =>
        Path.Combine(ConfigDirectory, "runtime");

    public static string SupportDirectory =>
        Path.Combine(ConfigDirectory, "support");

    public static string RedistDirectory =>
        Path.Combine(ConfigDirectory, "redist");

    public static string GetSupportDirectory(string gameId)
    {
        EnsureSafeGameId(gameId);
        return Path.Combine(SupportDirectory, gameId);
    }

    public static string GetInstalledManifestPath(string gameId)
    {
        EnsureSafeGameId(gameId);
        return Path.Combine(
            RuntimeConfigPath,
            "heroic_gogdl",
            "manifests",
            gameId);
    }

    /// <summary>
    /// Removes only gogdl's per-game installation memory. The account session,
    /// remote library cache, artwork, and shared redistributables remain intact.
    /// Without this cleanup gogdl can compare a deleted game against its stale
    /// manifest and incorrectly report "Nothing to do" on reinstall.
    /// </summary>
    public static void ClearInstalledState(string gameId)
    {
        var manifestPath = GetInstalledManifestPath(gameId);
        var supportDirectory = GetSupportDirectory(gameId);
        if (File.Exists(manifestPath))
        {
            File.Delete(manifestPath);
        }

        if (Directory.Exists(supportDirectory))
        {
            Directory.Delete(supportDirectory, recursive: true);
        }
    }

    public static string ResolveExistingToolPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return File.Exists(ToolPath) && HasExpectedHash(ToolPath)
            ? ToolPath
            : string.Empty;
    }

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
                $"gogdl-{Version}-{Guid.NewGuid():N}.tmp");

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
                        "The downloaded GOG helper failed its integrity check.");
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

    public static string Authenticate(string authorizationCode)
    {
        if (string.IsNullOrWhiteSpace(authorizationCode))
        {
            throw new InvalidOperationException("GOG did not return an authorization code.");
        }

        Directory.CreateDirectory(ConfigDirectory);
        var toolPath = EnsureInstalled();
        var startInfo = CreateStartInfo(toolPath);
        startInfo.ArgumentList.Add("--auth-config-path");
        startInfo.ArgumentList.Add(AuthPath);
        startInfo.ArgumentList.Add("auth");
        startInfo.ArgumentList.Add("--code");
        startInfo.ArgumentList.Add(authorizationCode.Trim());

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit(120000);
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new InvalidOperationException("GOG sign-in timed out.");
        }

        Task.WaitAll(outputTask, errorTask);
        if (process.ExitCode != 0 || !File.Exists(AuthPath))
        {
            var message = new[] { errorTask.Result, outputTask.Result }
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?.Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message) ? "GOG sign-in failed." : message);
        }

        return toolPath;
    }

    public static void ClearAuthentication()
    {
        try
        {
            if (File.Exists(AuthPath))
            {
                File.Delete(AuthPath);
            }
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "GOG credentials are still in use. Close the GOG sign-in window and try again.",
                exception);
        }
    }

    public static void ConfigureEnvironment(ProcessStartInfo startInfo)
    {
        Directory.CreateDirectory(RuntimeConfigPath);
        startInfo.Environment["GOGDL_CONFIG_PATH"] = RuntimeConfigPath;
    }

    private static ProcessStartInfo CreateStartInfo(string toolPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = toolPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        ConfigureEnvironment(startInfo);
        return startInfo;
    }

    private static bool HasExpectedHash(string path)
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

    private static void EnsureSafeGameId(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId) ||
            !Regex.IsMatch(
                gameId,
                "^[A-Za-z0-9_.-]+$",
                RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException(
                "The GOG game identifier is invalid.");
        }
    }
}
