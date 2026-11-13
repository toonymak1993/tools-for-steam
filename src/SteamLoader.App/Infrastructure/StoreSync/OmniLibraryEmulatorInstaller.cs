using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed record OmniLibraryEmulatorInstallStatus(
    string State,
    int ProgressPercent,
    string Message,
    DateTimeOffset UpdatedAtUtc)
{
    public static OmniLibraryEmulatorInstallStatus Idle { get; } =
        new("idle", 0, string.Empty, DateTimeOffset.MinValue);

    public bool IsActive => State is "queued" or "waiting" or "downloading" or "extracting" or "installing-core" or "verifying";
}

/// <summary>
/// Installs emulators from their official Windows x64 packages (ported from
/// ORBIT's retro setup): GitHub releases/latest with the published SHA-256
/// digest, pinned RetroArch/Dolphin/Project64 builds with pinned checksums,
/// and RetroArch cores from the libretro buildbot. Packages are validated
/// before extraction, unpacked into Documents\Tools for Steam\Emulators\&lt;id&gt;
/// and verified by detection afterwards. One install runs at a time;
/// automatic installs wait while a game is running.
/// </summary>
internal static class OmniLibraryEmulatorInstaller
{
    private const long MaximumDownloadBytes = 512L * 1024 * 1024;
    private const long MaximumExpandedBytes = 3L * 1024 * 1024 * 1024;
    private const int MaximumArchiveEntries = 100_000;
    private const long MaximumApiBytes = 4L * 1024 * 1024;
    private static readonly TimeSpan AutomaticRetryAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(20);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, OmniLibraryEmulatorInstallStatus> Statuses =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastFailures =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<InstallJob> Jobs = new();
    private static Task? Worker;

    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>Raised (on a worker thread) whenever an install changes state.</summary>
    public static event Action? Changed;

    private sealed record InstallJob(string SystemId, string EmulatorId, bool Automatic);

    public static OmniLibraryEmulatorInstallStatus GetStatus(string emulatorId)
    {
        lock (Gate)
        {
            return Statuses.TryGetValue(emulatorId, out var status)
                ? status
                : OmniLibraryEmulatorInstallStatus.Idle;
        }
    }

    /// <summary>
    /// Queues an install. Automatic requests are dropped while the same
    /// emulator is already queued or failed recently; explicit requests always
    /// retry.
    /// </summary>
    public static bool Request(string systemId, string emulatorId, bool automatic)
    {
        var system = OmniLibraryRomSystemRegistry.GetRequired(systemId);
        var emulator = OmniLibraryEmulatorCatalog.GetRequired(emulatorId);
        if (!system.EmulatorIds.Contains(emulator.Id, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{emulator.Title} does not run {system.Title} games.");
        }

        if (!emulator.CanInstall)
        {
            throw new InvalidOperationException($"TFS cannot install {emulator.Title} automatically. Install it yourself and select its executable.");
        }

        if (!Environment.Is64BitOperatingSystem || !OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Automatic emulator installation requires 64-bit Windows.");
        }

        lock (Gate)
        {
            if (Jobs.Any(job => job.EmulatorId.Equals(emulator.Id, StringComparison.OrdinalIgnoreCase) &&
                                job.SystemId.Equals(system.Id, StringComparison.OrdinalIgnoreCase)) ||
                (Statuses.TryGetValue(emulator.Id, out var current) && current.IsActive && current.State != "queued" && automatic))
            {
                return false;
            }

            if (automatic &&
                LastFailures.TryGetValue(emulator.Id, out var failedAt) &&
                DateTimeOffset.UtcNow - failedAt < AutomaticRetryAfter)
            {
                return false;
            }

            Jobs.Enqueue(new InstallJob(system.Id, emulator.Id, automatic));
            if (!(Statuses.TryGetValue(emulator.Id, out var status) && status.IsActive))
            {
                Statuses[emulator.Id] = new("queued", 0, $"Waiting to install {emulator.Title}…", DateTimeOffset.UtcNow);
            }

            if (Worker is null || Worker.IsCompleted)
            {
                Worker = Task.Run(RunWorkerAsync);
            }
        }

        RaiseChanged();
        return true;
    }

    private static async Task RunWorkerAsync()
    {
        while (true)
        {
            InstallJob job;
            lock (Gate)
            {
                if (Jobs.Count == 0)
                {
                    Worker = null;
                    return;
                }

                job = Jobs.Dequeue();
            }

            var emulator = OmniLibraryEmulatorCatalog.GetRequired(job.EmulatorId);
            try
            {
                if (job.Automatic)
                {
                    // Never download hundreds of megabytes while someone plays.
                    while (OmniLibraryGameActivity.IsGameActive())
                    {
                        SetStatus(emulator.Id, "waiting", 0, $"{emulator.Title} installs after the current game.");
                        await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                    }
                }

                using var timeout = new CancellationTokenSource(InstallTimeout);
                await InstallAsync(
                    OmniLibraryRomSystemRegistry.GetRequired(job.SystemId),
                    emulator,
                    timeout.Token).ConfigureAwait(false);
                lock (Gate)
                {
                    LastFailures.Remove(emulator.Id);
                }

                SetStatus(emulator.Id, "ready", 100, $"{emulator.Title} is installed.");
            }
            catch (Exception exception)
            {
                lock (Gate)
                {
                    LastFailures[emulator.Id] = DateTimeOffset.UtcNow;
                }

                var reason = exception is OperationCanceledException
                    ? "The installation took too long."
                    : exception.Message.Trim();
                SetStatus(emulator.Id, "failed", 0, $"{emulator.Title} could not be installed: {reason}");
            }
            finally
            {
                OmniLibraryEmulatorDetector.Invalidate();
            }
        }
    }

    private static async Task InstallAsync(
        OmniLibraryRomSystemDescriptor system,
        OmniLibraryEmulatorDescriptor emulator,
        CancellationToken cancellationToken)
    {
        OmniLibraryEmulatorDetector.Invalidate();
        SetStatus(emulator.Id, "verifying", 0, $"Checking {emulator.Title}…");
        var existing = OmniLibraryEmulatorDetector.Locate(emulator);
        var temporary = Path.Combine(Path.GetTempPath(), $"tfs-emulator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            if (existing.Length == 0)
            {
                SetStatus(emulator.Id, "downloading", 0, $"Finding the official {emulator.Title} package…");
                var package = await ResolvePackageAsync(emulator, cancellationToken).ConfigureAwait(false);
                var archive = Path.Combine(temporary, package.FileName);
                await DownloadAsync(
                    package.Url,
                    archive,
                    package.ExpectedBytes,
                    (received, total) => SetStatus(
                        emulator.Id,
                        "downloading",
                        total > 0 ? (int)Math.Clamp(received * 100 / total, 0, 99) : 0,
                        total > 0
                            ? $"Downloading {emulator.Title}: {received / 1048576d:0} of {total / 1048576d:0} MB"
                            : $"Downloading {emulator.Title}: {received / 1048576d:0} MB"),
                    cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(package.Sha256) &&
                    !(await Sha256Async(archive, cancellationToken).ConfigureAwait(false))
                        .Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The download does not match the published checksum.");
                }

                SetStatus(emulator.Id, "extracting", 100, $"Unpacking {emulator.Title}…");
                var extracted = Path.Combine(temporary, "extracted");
                await ExtractAsync(archive, extracted, cancellationToken).ConfigureAwait(false);
                var executable = OmniLibraryEmulatorDetector.NestedExecutable([extracted], emulator.ExecutableNames, 8);
                if (executable.Length == 0)
                {
                    throw new InvalidDataException("The official package does not contain the expected emulator executable.");
                }

                var target = OmniLibraryEmulatorDetector.ManagedDirectory(emulator.Id);
                CopyDirectory(Path.GetDirectoryName(executable)!, target, emulator.Title, cancellationToken);
                OmniLibraryEmulatorCatalog.ConfigurePortableMode(
                    emulator.Id,
                    Path.Combine(target, Path.GetFileName(executable)));
                OmniLibraryEmulatorDetector.Invalidate();
                existing = OmniLibraryEmulatorDetector.Locate(emulator);
                if (existing.Length == 0)
                {
                    throw new InvalidOperationException("The emulator was unpacked but TFS cannot find it.");
                }
            }

            if (emulator.Id == OmniLibraryEmulatorCatalog.RetroArchId &&
                OmniLibraryEmulatorDetector.MatchingRetroArchCore(
                    system,
                    OmniLibraryEmulatorDetector.RetroArchCoreFiles(existing)).Length == 0)
            {
                await InstallRetroArchCoreAsync(system, temporary, cancellationToken).ConfigureAwait(false);
            }

            SetStatus(emulator.Id, "verifying", 100, $"Checking {emulator.Title}…");
            OmniLibraryEmulatorDetector.Invalidate();
            var selection = OmniLibraryEmulatorDetector.Resolve(system, emulator.Id, null);
            if (!selection.Ready)
            {
                throw new InvalidOperationException($"{emulator.Title} is installed but not ready for {system.Title}.");
            }
        }
        finally
        {
            TryDeleteDirectory(temporary);
        }
    }

    private sealed record ResolvedPackage(string Url, string FileName, long? ExpectedBytes, string Sha256);

    private static async Task<ResolvedPackage> ResolvePackageAsync(
        OmniLibraryEmulatorDescriptor emulator,
        CancellationToken cancellationToken)
    {
        var package = emulator.Package;
        switch (package.Kind)
        {
            case OmniLibraryEmulatorPackageKind.RetroArchStable:
                return new(
                    OmniLibraryEmulatorCatalog.ValidatedDownloadUrl(
                        $"https://buildbot.libretro.com/stable/{package.Version}/windows/x86_64/RetroArch.7z"),
                    $"RetroArch-{package.Version}.7z",
                    null,
                    package.Sha256);
            case OmniLibraryEmulatorPackageKind.DolphinStable:
                return new(
                    OmniLibraryEmulatorCatalog.ValidatedDownloadUrl(
                        $"https://dl.dolphin-emu.org/releases/{package.Version}/dolphin-{package.Version}-x64.7z"),
                    $"dolphin-{package.Version}-x64.7z",
                    null,
                    package.Sha256);
            case OmniLibraryEmulatorPackageKind.Project64Stable:
            {
                var fileVersion = package.Version.Replace('.', '-');
                return new(
                    OmniLibraryEmulatorCatalog.ValidatedDownloadUrl(
                        $"https://www.pj64-emu.com/file/project64-{fileVersion}/"),
                    $"project64-{fileVersion}.zip",
                    null,
                    package.Sha256);
            }
            case OmniLibraryEmulatorPackageKind.DirectPackage:
                return new(
                    OmniLibraryEmulatorCatalog.ValidatedDownloadUrl(package.Url),
                    SafeFileName(package.FileName),
                    null,
                    package.Sha256);
            case OmniLibraryEmulatorPackageKind.GithubRelease:
                return await ResolveGithubReleaseAsync(package, cancellationToken).ConfigureAwait(false);
            default:
                throw new InvalidOperationException("Automatic installation is unavailable for this emulator.");
        }
    }

    private static async Task<ResolvedPackage> ResolveGithubReleaseAsync(
        OmniLibraryEmulatorPackage package,
        CancellationToken cancellationToken)
    {
        var url = OmniLibraryEmulatorCatalog.ValidatedDownloadUrl(
            $"https://api.github.com/repos/{package.Repository}/releases/latest");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub returned HTTP {(int)response.StatusCode} for the release list.");
        }

        if (response.Content.Headers.ContentLength is > MaximumApiBytes)
        {
            throw new InvalidDataException("The release list is unexpectedly large.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return SelectGithubAsset(package, document.RootElement);
    }

    internal static ResolvedPackageInfo SelectGithubAssetInfo(OmniLibraryEmulatorPackage package, JsonElement release)
    {
        var resolved = SelectGithubAsset(package, release);
        return new(resolved.Url, resolved.FileName, resolved.ExpectedBytes, resolved.Sha256);
    }

    internal sealed record ResolvedPackageInfo(string Url, string FileName, long? ExpectedBytes, string Sha256);

    private static ResolvedPackage SelectGithubAsset(OmniLibraryEmulatorPackage package, JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The official release has no package list.");
        }

        var matches = assets.EnumerateArray()
            .Where(asset => asset.ValueKind == JsonValueKind.Object &&
                            asset.TryGetProperty("name", out var name) &&
                            name.ValueKind == JsonValueKind.String &&
                            package.AssetPattern?.IsMatch(name.GetString() ?? string.Empty) == true)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException($"The official Windows package of {package.Repository} could not be identified.");
        }

        var asset = matches[0];
        var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement) &&
                          urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new InvalidDataException("The official package has no download address.");
        }

        long? size = asset.TryGetProperty("size", out var sizeElement) &&
                     sizeElement.ValueKind == JsonValueKind.Number &&
                     sizeElement.TryGetInt64(out var bytes) && bytes > 0
            ? bytes
            : null;
        if (size > MaximumDownloadBytes)
        {
            throw new InvalidDataException("The emulator package is too large.");
        }

        var digest = asset.TryGetProperty("digest", out var digestElement) &&
                     digestElement.ValueKind == JsonValueKind.String
            ? digestElement.GetString() ?? string.Empty
            : string.Empty;
        var sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
                     digest.Length == "sha256:".Length + 64
            ? digest["sha256:".Length..]
            : string.Empty;
        return new(
            OmniLibraryEmulatorCatalog.ValidatedDownloadUrl(downloadUrl),
            SafeFileName(asset.GetProperty("name").GetString()!),
            size,
            sha256);
    }

    private static async Task DownloadAsync(
        string url,
        string destination,
        long? expectedBytes,
        Action<long, long> progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OmniLibraryEmulatorCatalog.ValidatedDownloadUrl(url));
        request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is { } finalUri)
        {
            OmniLibraryEmulatorCatalog.ValidatedDownloadUrl(finalUri.AbsoluteUri);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException("The official package is no longer available.");
        }

        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? expectedBytes ?? 0;
        if (total > MaximumDownloadBytes)
        {
            throw new InvalidDataException("The emulator package is too large.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long received = 0;
        var lastReport = Stopwatch.StartNew();
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            received += read;
            if (received > MaximumDownloadBytes)
            {
                throw new InvalidDataException("The emulator package is too large.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (lastReport.ElapsedMilliseconds >= 500)
            {
                progress(received, total);
                lastReport.Restart();
            }
        }

        if (expectedBytes is { } expected && received != expected)
        {
            throw new InvalidDataException("The download is incomplete.");
        }

        progress(received, total == 0 ? received : total);
    }

    private static async Task ExtractAsync(string archive, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var extension = Path.GetExtension(archive);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ExtractZip(archive, destination, cancellationToken);
        }
        else
        {
            var sevenZip = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? ExtractSevenZipFromSelfExtractor(archive)
                : archive;
            await ExtractWithWindowsTarAsync(sevenZip, destination, cancellationToken).ConfigureAwait(false);
        }

        ValidateExtractedTree(destination);
    }

    private static void ExtractZip(string archive, string destination, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count == 0 || zip.Entries.Count > MaximumArchiveEntries)
        {
            throw new InvalidDataException("The emulator package has an unexpected number of files.");
        }

        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            ValidateEntryPath(entry.FullName);
            expanded += entry.Length;
            if (expanded > MaximumExpandedBytes)
            {
                throw new InvalidDataException("The emulator package expands beyond the safety limit.");
            }
        }

        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The emulator package escaped its installation folder.");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>
    /// MAME ships as a 7-Zip self-extractor. 7z offsets are relative to the
    /// signature header, so the archive starts at the signature.
    /// </summary>
    private static string ExtractSevenZipFromSelfExtractor(string executable)
    {
        byte[] signature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
        using var input = File.OpenRead(executable);
        var window = new byte[Math.Min(input.Length, 8L * 1024 * 1024)];
        var read = input.Read(window, 0, window.Length);
        var offset = window.AsSpan(0, read).IndexOf(signature);
        if (offset < 0)
        {
            throw new InvalidDataException("The self-extracting package contains no 7-Zip archive.");
        }

        var target = Path.ChangeExtension(executable, ".7z");
        input.Position = offset;
        using var output = File.Create(target);
        input.CopyTo(output);
        return target;
    }

    /// <summary>
    /// Windows' own bsdtar (libarchive) reads 7z, so TFS needs no bundled
    /// 7-Zip. Entries are listed and checked before anything is written.
    /// </summary>
    private static async Task ExtractWithWindowsTarAsync(string archive, string destination, CancellationToken cancellationToken)
    {
        var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        if (!File.Exists(tar))
        {
            throw new InvalidOperationException("Windows' tar.exe is missing, so this package cannot be unpacked.");
        }

        var listing = await RunAsync(tar, ["-tf", archive], cancellationToken).ConfigureAwait(false);
        var entries = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0 || entries.Length > MaximumArchiveEntries)
        {
            throw new InvalidDataException("The emulator package has an unexpected number of files.");
        }

        foreach (var entry in entries)
        {
            ValidateEntryPath(entry);
        }

        await RunAsync(tar, ["-xf", archive, "-C", destination], cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The unpacker could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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

            throw;
        }

        if (process.ExitCode != 0)
        {
            var message = (await error.ConfigureAwait(false)).Trim();
            throw new InvalidDataException(string.IsNullOrWhiteSpace(message) ? "The package could not be unpacked." : message);
        }

        return await output.ConfigureAwait(false);
    }

    internal static void ValidateEntryPath(string entryPath)
    {
        var normalized = entryPath.Replace('\\', '/').Normalize(System.Text.NormalizationForm.FormKC);
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Contains('\0') ||
            normalized.StartsWith('/') ||
            (normalized.Length >= 2 && normalized[1] == ':') ||
            normalized.Split('/').Any(part => part == ".."))
        {
            throw new InvalidDataException("The emulator package contains an unsafe path.");
        }
    }

    private static void ValidateExtractedTree(string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long expanded = 0;
        var count = 0;
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if (++count > MaximumArchiveEntries)
            {
                throw new InvalidDataException("The emulator package has too many files.");
            }

            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The emulator package contains an unsafe link.");
            }

            if (!Path.GetFullPath(entry.FullName).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The emulator package escaped its installation folder.");
            }

            if (entry is FileInfo file && (expanded += file.Length) > MaximumExpandedBytes)
            {
                throw new InvalidDataException("The emulator package expands beyond the safety limit.");
            }
        }
    }

    /// <summary>
    /// Updates in place: files from the package overwrite older program
    /// files, while saves and settings the emulator created stay untouched.
    /// </summary>
    private static void CopyDirectory(string source, string target, string title, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            try
            {
                File.Copy(file, destination, overwrite: true);
            }
            catch (IOException)
            {
                throw new IOException($"Close {title} and try again; its files are in use.");
            }
        }
    }

    private static async Task InstallRetroArchCoreAsync(
        OmniLibraryRomSystemDescriptor system,
        string temporary,
        CancellationToken cancellationToken)
    {
        if (system.RetroArchCores.Count == 0)
        {
            throw new InvalidOperationException($"RetroArch has no core for {system.Title}.");
        }

        var coreDirectory = Path.Combine(
            OmniLibraryEmulatorDetector.ManagedDirectory(OmniLibraryEmulatorCatalog.RetroArchId),
            "cores");
        foreach (var core in system.RetroArchCores)
        {
            var coreFile = $"{core}_libretro.dll";
            SetStatus(OmniLibraryEmulatorCatalog.RetroArchId, "installing-core", 0, $"Downloading the RetroArch core {core}…");
            var archive = Path.Combine(temporary, $"{core}.zip");
            try
            {
                await DownloadAsync(
                    $"https://buildbot.libretro.com/nightly/windows/x86_64/latest/{coreFile}.zip",
                    archive,
                    null,
                    (received, total) => SetStatus(
                        OmniLibraryEmulatorCatalog.RetroArchId,
                        "installing-core",
                        total > 0 ? (int)Math.Clamp(received * 100 / total, 0, 99) : 0,
                        $"Downloading the RetroArch core {core}…"),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            var extracted = Path.Combine(temporary, $"core-{core}");
            await ExtractAsync(archive, extracted, cancellationToken).ConfigureAwait(false);
            var dll = Directory.EnumerateFiles(extracted, coreFile, SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidDataException("The RetroArch core package is incomplete.");
            Directory.CreateDirectory(coreDirectory);
            var staged = Path.Combine(coreDirectory, $".tfs-installing-{Guid.NewGuid():N}.dll");
            File.Copy(dll, staged);
            File.Move(staged, Path.Combine(coreDirectory, coreFile), overwrite: true);
            OmniLibraryEmulatorDetector.Invalidate();
            return;
        }

        throw new InvalidOperationException($"No RetroArch core for {system.Title} is available right now.");
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static string SafeFileName(string value)
    {
        var fileName = Path.GetFileName(value);
        if (!fileName.Equals(value, StringComparison.Ordinal) ||
            fileName.Length > 180 ||
            !System.Text.RegularExpressions.Regex.IsMatch(fileName, "^[a-z\\d][a-z\\d ._()+-]*\\.(?:7z|exe|zip)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            throw new InvalidDataException("The emulator package has an unexpected name.");
        }

        return fileName;
    }

    private static void SetStatus(string emulatorId, string state, int progress, string message)
    {
        lock (Gate)
        {
            Statuses[emulatorId] = new(state, progress, message, DateTimeOffset.UtcNow);
        }

        RaiseChanged();
    }

    private static void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            var root = Path.GetFullPath(Path.GetTempPath());
            var full = Path.GetFullPath(path);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(full).StartsWith("tfs-emulator-", StringComparison.Ordinal) &&
                Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        {
            Timeout = TimeSpan.FromMinutes(15),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ToolsForSteam-Emulation/1.0");
        return client;
    }
}
