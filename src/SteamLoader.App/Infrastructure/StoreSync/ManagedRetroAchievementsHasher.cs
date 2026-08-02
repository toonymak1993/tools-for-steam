using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Installs and runs RetroAchievements' pinned RAHasher build. RetroAchievements
/// identifies disc images by platform-specific content hashes; hashing a file
/// name or the complete ISO would produce incorrect results for several systems.
/// </summary>
internal static class ManagedRetroAchievementsHasher
{
    internal const string Version = "1.8.3";
    internal const string DownloadUrl =
        "https://github.com/RetroAchievements/RALibretro/releases/download/1.8.3/RAHasher-x64-Windows-1.8.3.zip";
    internal const string ArchiveSha256 =
        "d79be62a6d6a4b938c71fb2e7534fe3e1802ee7d97411fde2ee10b8e330dd93c";
    internal const string ExecutableSha256 =
        "23e2181a671387280f85678eecf00d3e877d3c0826322532fc353d4a108f3591";

    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static readonly SemaphoreSlim HashGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, string> HashCache =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MaximumCachedHashes = 2048;
    private const long MaximumArchiveBytes = 32L * 1024 * 1024;
    private const long MaximumExecutableBytes = 16L * 1024 * 1024;
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(3),
    };

    public static string ToolDirectory => Path.Combine(
        AppContext.BaseDirectory,
        "data",
        "omnilibrary",
        "helpers");

    public static string ToolPath => Path.Combine(ToolDirectory, "RAHasher.exe");

    public static async Task<string> HashAsync(
        string platformId,
        string romPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
        {
            throw new FileNotFoundException(
                "The ROM file is no longer available for RetroAchievements identification.",
                romPath);
        }

        var systemId = ResolveSystemId(platformId);
        if (systemId == 0)
        {
            throw new InvalidOperationException(
                "RetroAchievements hashing is not configured for this emulation system yet.");
        }

        var file = new FileInfo(romPath);
        var fullPath = file.FullName;
        var cacheKey = string.Join(
            "|",
            systemId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            fullPath,
            file.Length.ToString("x16", System.Globalization.CultureInfo.InvariantCulture),
            file.LastWriteTimeUtc.Ticks.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
        if (HashCache.TryGetValue(cacheKey, out var cachedHash))
        {
            return cachedHash;
        }

        await HashGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HashCache.TryGetValue(cacheKey, out cachedHash))
            {
                return cachedHash;
            }

            var toolPath = await EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);
            var startInfo = new ProcessStartInfo
            {
                FileName = toolPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add(systemId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(fullPath);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "RetroAchievements could not start its ROM identification helper.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException(
                    "RetroAchievements ROM identification timed out.");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var output = (await outputTask.ConfigureAwait(false)).Trim();
            var error = (await errorTask.ConfigureAwait(false)).Trim();
            var hash = Regex.Match(
                output,
                "(?im)^[0-9a-f]{32}$",
                RegexOptions.CultureInvariant).Value.ToLowerInvariant();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(hash))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? "RetroAchievements could not identify this ROM format."
                        : error);
            }

            if (HashCache.Count >= MaximumCachedHashes)
            {
                HashCache.Clear();
            }
            HashCache[cacheKey] = hash;
            return hash;
        }
        finally
        {
            HashGate.Release();
        }
    }

    private static async Task<string> EnsureInstalledAsync(
        CancellationToken cancellationToken)
    {
        await InstallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasExpectedHash(ToolPath, ExecutableSha256))
            {
                return ToolPath;
            }

            Directory.CreateDirectory(ToolDirectory);
            var archivePath = Path.Combine(
                ToolDirectory,
                $"rahasher-{Version}-{Guid.NewGuid():N}.zip.tmp");
            var executablePath = Path.Combine(
                ToolDirectory,
                $"rahasher-{Version}-{Guid.NewGuid():N}.exe.tmp");
            try
            {
                using var response = await HttpClient.GetAsync(
                    DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumArchiveBytes)
                {
                    throw new InvalidOperationException(
                        "The RetroAchievements helper download is unexpectedly large.");
                }
                await using (var source = await response.Content.ReadAsStreamAsync(
                                 cancellationToken).ConfigureAwait(false))
                await using (var destination = new FileStream(
                                 archivePath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 81920,
                                 useAsync: true))
                {
                    await CopyToWithLimitAsync(
                        source,
                        destination,
                        MaximumArchiveBytes,
                        cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (!HasExpectedHash(archivePath, ArchiveSha256))
                {
                    throw new InvalidOperationException(
                        "The downloaded RetroAchievements helper failed its integrity check.");
                }

                using (var archive = ZipFile.OpenRead(archivePath))
                {
                    var entry = archive.Entries.FirstOrDefault(candidate =>
                        Path.GetFileName(candidate.FullName).Equals(
                            "RAHasher.exe",
                            StringComparison.OrdinalIgnoreCase));
                    if (entry is null)
                    {
                        throw new InvalidOperationException(
                            "The RetroAchievements helper archive is incomplete.");
                    }
                    if (entry.Length <= 0 || entry.Length > MaximumExecutableBytes)
                    {
                        throw new InvalidOperationException(
                            "The RetroAchievements helper archive contains an invalid executable.");
                    }
                    entry.ExtractToFile(executablePath, overwrite: false);
                }

                if (!HasExpectedHash(executablePath, ExecutableSha256))
                {
                    throw new InvalidOperationException(
                        "The extracted RetroAchievements helper failed its integrity check.");
                }

                File.Move(executablePath, ToolPath, overwrite: true);
                return ToolPath;
            }
            finally
            {
                TryDelete(archivePath);
                TryDelete(executablePath);
            }
        }
        finally
        {
            InstallGate.Release();
        }
    }

    private static int ResolveSystemId(string? platformId) =>
        platformId?.Trim().ToLowerInvariant() switch
        {
            "nintendo-64" => 2,
            "game-boy-advance" => 5,
            "gamecube" => 16,
            "psp" => 41,
            _ => 0,
        };

    private static async Task CopyToWithLimitAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }
            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidOperationException(
                    "The RetroAchievements helper download exceeded its safety limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool HasExpectedHash(string path, string expected)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
