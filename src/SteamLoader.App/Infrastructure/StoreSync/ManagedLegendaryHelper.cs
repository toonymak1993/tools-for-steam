using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Owns OmniLibrary's pinned Legendary helper and its isolated sign-in state.
/// It never imports Epic Games Launcher credentials, because doing that can sign
/// the official launcher out.
/// </summary>
internal static class ManagedLegendaryHelper
{
    internal const string Version = "0.20.43";
    internal const string DownloadUrl =
        "https://github.com/Heroic-Games-Launcher/legendary/releases/download/0.20.43/legendary_windows_x86_64.exe";
    internal const string Sha256 =
        "ec1ad2d19d44e07b2b0330191c300979f102c509f2a889708099f453c5188f20";

    private static readonly object InstallGate = new();
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(3),
    };

    public static string ToolDirectory =>
        Path.Combine(AppContext.BaseDirectory, "data", "omnilibrary", "helpers");

    public static string ToolPath => Path.Combine(ToolDirectory, "legendary.exe");

    public static string ConfigDirectory =>
        Path.Combine(AppContext.BaseDirectory, "data", "omnilibrary", "legendary");

    public static string UserDataPath => Path.Combine(ConfigDirectory, "user.json");

    public static string ResolveExistingToolPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return File.Exists(ToolPath) ? ToolPath : string.Empty;
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
                $"legendary-{Version}-{Guid.NewGuid():N}.tmp");

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
                        "The downloaded Epic helper failed its integrity check.");
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

    public static void ConfigureEnvironment(ProcessStartInfo startInfo)
    {
        Directory.CreateDirectory(ConfigDirectory);
        startInfo.Environment["LEGENDARY_CONFIG_PATH"] = ConfigDirectory;
    }

    public static string Authenticate(string authorizationCode)
    {
        if (string.IsNullOrWhiteSpace(authorizationCode))
        {
            throw new InvalidOperationException("Epic did not return an authorization code.");
        }

        var toolPath = EnsureInstalled();
        var startInfo = new ProcessStartInfo
        {
            FileName = toolPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("auth");
        startInfo.ArgumentList.Add("--code");
        startInfo.ArgumentList.Add(authorizationCode.Trim());
        ConfigureEnvironment(startInfo);

        using var process = new Process
        {
            StartInfo = startInfo,
        };
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

            throw new InvalidOperationException("Epic sign-in timed out.");
        }

        Task.WaitAll(outputTask, errorTask);
        if (process.ExitCode != 0)
        {
            var message = new[] { errorTask.Result, outputTask.Result }
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?.Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message) ? "Epic sign-in failed." : message);
        }

        return toolPath;
    }

    public static void ClearAuthentication()
    {
        var toolPath = ResolveExistingToolPath(null);
        if (!string.IsNullOrWhiteSpace(toolPath))
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = toolPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                startInfo.ArgumentList.Add("auth");
                startInfo.ArgumentList.Add("--delete");
                ConfigureEnvironment(startInfo);
                using var process = Process.Start(startInfo);
                if (process is not null && !process.WaitForExit(60000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Local credential deletion below is still mandatory even if the
                // helper cannot revoke the session remotely.
            }
        }

        try
        {
            if (File.Exists(UserDataPath))
            {
                File.Delete(UserDataPath);
            }
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Epic credentials are still in use. Close the Epic sign-in window and try again.",
                exception);
        }
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
}
