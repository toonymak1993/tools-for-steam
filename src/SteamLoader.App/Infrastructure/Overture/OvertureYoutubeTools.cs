using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.Overture;

/// <summary>
/// YouTube support for TFS Overture: installs yt-dlp (and Deno, the JavaScript
/// runtime yt-dlp needs for YouTube) from their official GitHub releases,
/// checks each download against the release's published SHA-256, and runs
/// yt-dlp for search and audio-only downloads. Nothing is installed until the
/// user asks for it; the tools live in TFS' own data folder.
/// </summary>
public sealed partial class OvertureYoutubeTools : IDisposable
{
    internal const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    internal const string YtDlpSumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    internal const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
    internal const string DenoSumUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip.sha256sum";

    private static readonly TimeSpan AutomaticRetryAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(14);
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(4);
    private const long MaxToolBytes = 300L * 1024 * 1024;

    private readonly string _toolsDirectory;
    private readonly HttpClient _httpClient;
    private readonly object _gate = new();
    private Task? _installTask;
    private string _state = "idle";
    private string? _message;
    private DateTimeOffset _lastFailureAt = DateTimeOffset.MinValue;

    public OvertureYoutubeTools(string toolsDirectory)
    {
        _toolsDirectory = toolsDirectory;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ToolsForSteam-Overture/1.0");
    }

    public string YtDlpPath => Path.Combine(_toolsDirectory, "yt-dlp.exe");

    public string DenoPath => Path.Combine(_toolsDirectory, "deno.exe");

    public bool IsInstalled => File.Exists(YtDlpPath);

    public OvertureYoutubeStatus GetStatus()
    {
        lock (_gate)
        {
            DateTimeOffset? installedAt = File.Exists(YtDlpPath)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(YtDlpPath), TimeSpan.Zero)
                : null;
            return new OvertureYoutubeStatus(IsInstalled, File.Exists(DenoPath), _state, _message, installedAt);
        }
    }

    /// <summary>Starts (or joins) the installation/repair in the background.</summary>
    public OvertureYoutubeStatus StartInstall()
    {
        _ = BeginInstall();
        return GetStatus();
    }

    /// <summary>
    /// Installs yt-dlp and Deno the first time they are needed. A failed attempt
    /// is not repeated automatically for a while, so an offline PC does not
    /// download on every game page; the repair button always retries.
    /// </summary>
    public async Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        if (IsInstalled)
        {
            // YouTube changes often and old yt-dlp builds stop working; refresh
            // in the background now and then (the current copy keeps working).
            lock (_gate)
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(YtDlpPath) > RefreshAfter &&
                    (_installTask is null || _installTask.IsCompleted) &&
                    DateTimeOffset.UtcNow - _lastFailureAt >= AutomaticRetryAfter)
                {
                    _ = BeginInstall();
                }
            }

            return;
        }

        Task install;
        lock (_gate)
        {
            if (_installTask is { IsCompleted: true } && _state == "failed" &&
                DateTimeOffset.UtcNow - _lastFailureAt < AutomaticRetryAfter)
            {
                throw new InvalidOperationException(_message ?? "YouTube support could not be installed.");
            }

            install = BeginInstall();
        }

        await install.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!IsInstalled)
        {
            throw new InvalidOperationException(GetStatus().Message ?? "YouTube support could not be installed.");
        }
    }

    /// <summary>True when a game page may trigger the automatic installation.</summary>
    public bool CanInstallAutomatically
    {
        get
        {
            lock (_gate)
            {
                return IsInstalled || _state != "failed" || DateTimeOffset.UtcNow - _lastFailureAt >= AutomaticRetryAfter;
            }
        }
    }

    private Task BeginInstall()
    {
        lock (_gate)
        {
            if (_installTask is null || _installTask.IsCompleted)
            {
                _state = "installing";
                _message = null;
                _installTask = Task.Run(InstallAsync);
            }

            return _installTask;
        }
    }

    private async Task InstallAsync()
    {
        try
        {
            Directory.CreateDirectory(_toolsDirectory);
            SetState("installing", "Downloading yt-dlp…");
            var sums = await _httpClient.GetStringAsync(YtDlpSumsUrl).ConfigureAwait(false);
            var expected = FindChecksum(sums, "yt-dlp.exe")
                ?? throw new InvalidDataException("yt-dlp's checksum list has no entry for yt-dlp.exe.");
            await DownloadVerifiedAsync(YtDlpUrl, expected, YtDlpPath).ConfigureAwait(false);

            SetState("installing", "Downloading Deno…");
            var denoSum = await _httpClient.GetStringAsync(DenoSumUrl).ConfigureAwait(false);
            var denoExpected = FindChecksum(denoSum, null)
                ?? throw new InvalidDataException("Deno's checksum file could not be read.");
            var zipPath = Path.Combine(_toolsDirectory, "deno.zip.download");
            await DownloadVerifiedAsync(DenoUrl, denoExpected, zipPath).ConfigureAwait(false);
            try
            {
                ExtractDeno(zipPath, DenoPath);
            }
            finally
            {
                TryDelete(zipPath);
            }

            SetState("ready", null);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _lastFailureAt = DateTimeOffset.UtcNow;
            }

            SetState(IsInstalled ? "ready" : "failed", $"YouTube support could not be installed: {exception.Message}");
        }
    }

    private async Task DownloadVerifiedAsync(string url, string expectedSha256, string destination)
    {
        var temporary = $"{destination}.part";
        try
        {
            using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaxToolBytes)
                {
                    throw new InvalidDataException("The download is larger than expected.");
                }

                await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxToolBytes)
                    {
                        throw new InvalidDataException("The download is larger than expected.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                }
            }

            string actual;
            await using (var stream = File.OpenRead(temporary))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
            }

            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The checksum of {Path.GetFileName(url)} does not match the published one.");
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void ExtractDeno(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(item =>
            item.FullName.Equals("deno.exe", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("deno.exe is missing from the Deno archive.");
        var temporary = $"{destination}.part";
        entry.ExtractToFile(temporary, overwrite: true);
        File.Move(temporary, destination, overwrite: true);
    }

    /// <summary>
    /// Reads a SHA-256 out of a checksum file: the line naming <paramref name="fileName"/>
    /// in a "hash  name" list, or the first SHA-256 in a single-file checksum
    /// (any layout, including PowerShell's Get-FileHash table).
    /// </summary>
    internal static string? FindChecksum(string content, string? fileName)
    {
        foreach (var rawLine in (content ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            var match = Sha256Pattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (fileName is null)
            {
                return match.Value.ToUpperInvariant();
            }

            var rest = line[(match.Index + match.Length)..].Trim().TrimStart('*').Trim();
            if (rest.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return match.Value.ToUpperInvariant();
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<OvertureSearchResult>> SearchAsync(string query, int count, CancellationToken cancellationToken)
    {
        var cleanQuery = CleanQuery(query);
        if (cleanQuery.Length < 2)
        {
            return [];
        }

        var arguments = BaseArguments();
        arguments.AddRange(["--flat-playlist", "--dump-single-json", $"ytsearch{Math.Clamp(count, 1, 20)}:{cleanQuery}"]);
        var result = await RunAsync(arguments, SearchTimeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Output))
        {
            throw new InvalidOperationException(LastErrorLine(result.Error) ?? "The YouTube search failed.");
        }

        return ParseSearchResults(result.Output);
    }

    internal static IReadOnlyList<OvertureSearchResult> ParseSearchResults(string json)
    {
        var results = new List<OvertureSearchResult>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                var id = ReadString(entry, "id");
                var title = ReadString(entry, "title");
                if (!OvertureSettingsStore.IsValidVideoId(id) || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                double? duration = entry.TryGetProperty("duration", out var durationElement) &&
                    durationElement.ValueKind == JsonValueKind.Number &&
                    durationElement.TryGetDouble(out var seconds) && seconds > 0
                        ? seconds
                        : null;
                results.Add(new OvertureSearchResult(
                    id!,
                    title!.Trim(),
                    ReadString(entry, "channel") ?? ReadString(entry, "uploader"),
                    duration));
            }
        }
        catch (JsonException)
        {
        }

        return results;
    }

    /// <summary>Downloads the audio of one video into <paramref name="directory"/>; returns the file.</summary>
    public async Task<string> DownloadAudioAsync(string videoId, string directory, CancellationToken cancellationToken)
    {
        if (!OvertureSettingsStore.IsValidVideoId(videoId))
        {
            throw new ArgumentException("Invalid video id.", nameof(videoId));
        }

        Directory.CreateDirectory(directory);
        var arguments = BaseArguments();
        arguments.AddRange([
            "--no-playlist",
            "--no-progress",
            "-f", "bestaudio[ext=m4a]/bestaudio[ext=webm]/bestaudio",
            "--max-filesize", "80M",
            "-o", Path.Combine(directory, $"yt-{videoId}.%(ext)s"),
            "--print", "after_move:filepath",
            $"https://www.youtube.com/watch?v={videoId}"
        ]);
        var result = await RunAsync(arguments, DownloadTimeout, cancellationToken).ConfigureAwait(false);
        var path = result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException(LastErrorLine(result.Error) ?? "The download failed.");
        }

        var full = Path.GetFullPath(path);
        if (!IsInside(directory, full))
        {
            throw new InvalidOperationException("The download landed outside TFS Overture's folder.");
        }

        return full;
    }

    private List<string> BaseArguments()
    {
        var arguments = new List<string> { "--ignore-config", "--no-warnings", "--encoding", "utf-8" };
        if (File.Exists(DenoPath))
        {
            arguments.AddRange(["--js-runtimes", $"deno:{DenoPath}"]);
        }

        return arguments;
    }

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsInstalled)
        {
            throw new InvalidOperationException("YouTube support is not installed.");
        }

        var startInfo = new ProcessStartInfo(YtDlpPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = _toolsDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("yt-dlp could not be started.");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var output = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var error = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException("YouTube did not answer in time.");
        }
    }

    internal static string CleanQuery(string? query)
    {
        var text = new string((query ?? string.Empty).Where(character => !char.IsControl(character)).ToArray()).Trim();
        return text.Length > 120 ? text[..120] : text;
    }

    internal static bool IsInside(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string? LastErrorLine(string error) =>
        error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            ?.Replace("ERROR: ", string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void SetState(string state, string? message)
    {
        lock (_gate)
        {
            _state = state;
            _message = message;
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

    public void Dispose() => _httpClient.Dispose();

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    [GeneratedRegex("\\b[0-9A-Fa-f]{64}\\b")]
    private static partial Regex Sha256Pattern();
}
