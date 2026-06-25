using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamLoader.App.Models;

namespace SteamLoader.App.Services;

public sealed class ReleaseUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Uri ReleasesUri = new(
        $"https://api.github.com/repos/{SteamLoaderRuntime.ReleaseRepository}/releases?per_page=20");

    private readonly HttpClient _httpClient;

    public ReleaseUpdateService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ToolsForSteam-Updater/1.0");
    }

    public Task<UpdateCheckSnapshot> CheckAsync(CancellationToken cancellationToken = default)
    {
        return CheckAsync(SteamLoaderRuntime.UpdateChannelStable, cancellationToken);
    }

    public async Task<UpdateCheckSnapshot> CheckAsync(
        string channel,
        CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentVersion();
        var normalizedChannel = NormalizeUpdateChannel(channel);

        try
        {
            var release = await GetPreferredReleaseAsync(normalizedChannel, cancellationToken);
            var latestVersion = NormalizeVersion(release.TagName);
            var asset = FindUpdateAsset(release);
            var updateAvailable = IsNewerVersion(latestVersion, currentVersion);
            var releaseKind = release.Prerelease ? "preview" : "release";
            var channelLabel = GetChannelLabel(normalizedChannel);

            var message = updateAvailable
                ? asset is null
                    ? $"The latest {channelLabel} {releaseKind} is {latestVersion}, but no Windows package was attached."
                    : $"The latest {channelLabel} {releaseKind} is {latestVersion}."
                : normalizedChannel == SteamLoaderRuntime.UpdateChannelBeta && !release.Prerelease
                    ? $"No GitHub preview is published right now. Tools for Steam is following the latest stable release ({currentVersion})."
                    : $"You are already on the latest {channelLabel} {releaseKind} ({currentVersion}).";

            return BuildSnapshot(
                normalizedChannel,
                currentVersion,
                release,
                asset,
                updateAvailable,
                message);
        }
        catch (Exception exception)
        {
            return BuildFailureSnapshot(currentVersion, normalizedChannel, $"Update check failed: {exception.Message}");
        }
    }

    public Task<UpdateCheckSnapshot> BeginInstallLatestAsync(
        string installDirectory,
        string executablePath,
        IEnumerable<int> processIdsToWaitFor,
        CancellationToken cancellationToken = default)
    {
        return BeginInstallLatestAsync(
            SteamLoaderRuntime.UpdateChannelStable,
            installDirectory,
            executablePath,
            processIdsToWaitFor,
            progressCallback: null,
            cancellationToken);
    }

    public async Task<UpdateCheckSnapshot> BeginInstallLatestAsync(
        string channel,
        string installDirectory,
        string executablePath,
        IEnumerable<int> processIdsToWaitFor,
        Func<UpdateCheckSnapshot, Task>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedChannel = NormalizeUpdateChannel(channel);
        var currentVersion = GetCurrentVersion();
        var workDirectory = string.Empty;

        try
        {
            var release = await GetPreferredReleaseAsync(normalizedChannel, cancellationToken);
            var latestVersion = NormalizeVersion(release.TagName);
            var asset = FindUpdateAsset(release)
                ?? throw new InvalidOperationException("The selected GitHub release does not contain a Tools for Steam Windows package.");
            var baseSnapshot = BuildSnapshot(
                normalizedChannel,
                currentVersion,
                release,
                asset,
                updateAvailable: true,
                message: $"Preparing {latestVersion} from the {GetChannelLabel(normalizedChannel)} channel.");

            await ReportInstallProgressAsync(
                progressCallback,
                BuildInstallProgressSnapshot(
                    baseSnapshot,
                    "preparing",
                    5,
                    $"Preparing {latestVersion} from the {GetChannelLabel(normalizedChannel)} channel."));

            workDirectory = Path.Combine(Path.GetTempPath(), $"ToolsForSteam-Update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDirectory);

            var packagePath = Path.Combine(workDirectory, asset.Name);
            await ReportInstallProgressAsync(
                progressCallback,
                BuildInstallProgressSnapshot(
                    baseSnapshot,
                    "downloading",
                    10,
                    $"Downloading {asset.Name}..."));
            await DownloadFileAsync(
                asset.BrowserDownloadUrl,
                packagePath,
                async (totalBytes, downloadedBytes) =>
                {
                    var percent = MapDownloadProgressToInstallPercent(totalBytes, downloadedBytes);
                    var message = BuildDownloadProgressMessage(asset.Name, totalBytes, downloadedBytes);
                    await ReportInstallProgressAsync(
                        progressCallback,
                        BuildInstallProgressSnapshot(baseSnapshot, "downloading", percent, message));
                },
                cancellationToken);

            var isInstallerAsset = IsInstallerAsset(asset);
            await ReportInstallProgressAsync(
                progressCallback,
                BuildInstallProgressSnapshot(
                    baseSnapshot,
                    "validating",
                    84,
                    $"Validating {asset.Name}..."));
            ValidateDownloadedPackage(packagePath, isInstallerAsset);

            var scriptPath = Path.Combine(workDirectory, "apply-update.ps1");
            await ReportInstallProgressAsync(
                progressCallback,
                BuildInstallProgressSnapshot(
                    baseSnapshot,
                    "preparing",
                    92,
                    "Preparing the installer handoff..."));
            File.WriteAllText(
                scriptPath,
                BuildUpdateScript(
                    workDirectory,
                    packagePath,
                    installDirectory,
                    Path.GetFileName(executablePath),
                    processIdsToWaitFor,
                    isInstallerAsset));

            await ReportInstallProgressAsync(
                progressCallback,
                BuildInstallProgressSnapshot(
                    baseSnapshot,
                    "installing",
                    97,
                    "Launching the installer and restarting Tools for Steam..."));
            using var handoffProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (handoffProcess is null)
            {
                throw new InvalidOperationException("The update handoff process could not be started.");
            }

            return BuildSnapshot(
                normalizedChannel,
                currentVersion,
                release,
                asset,
                updateAvailable: true,
                message: isInstallerAsset
                    ? $"Installing {latestVersion} from the {GetChannelLabel(normalizedChannel)} channel. Tools for Steam will restart after setup finishes."
                    : $"Applying {latestVersion} from the {GetChannelLabel(normalizedChannel)} channel. Tools for Steam will restart after the files are replaced.")
                with
                {
                    InstallInProgress = true,
                    InstallState = "installing",
                    InstallProgressPercent = 100
                };
        }
        catch
        {
            TryDeleteWorkDirectory(workDirectory);
            throw;
        }
    }

    private async Task<GithubRelease> GetPreferredReleaseAsync(string channel, CancellationToken cancellationToken)
    {
        var releases = await RetryUpdateNetworkOperationAsync(
            () => GetReleasesAsync(cancellationToken),
            cancellationToken);
        return SelectRelease(releases, channel);
    }

    private async Task<IReadOnlyList<GithubRelease>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        await using var stream = await _httpClient.GetStreamAsync(ReleasesUri, cancellationToken);
        return await JsonSerializer.DeserializeAsync<IReadOnlyList<GithubRelease>>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty releases response.");
    }

    private static GithubRelease SelectRelease(IReadOnlyList<GithubRelease> releases, string channel)
    {
        var publishedReleases = releases
            .Where(release => !release.Draft)
            .ToArray();

        if (publishedReleases.Length == 0)
        {
            throw new InvalidOperationException("No published GitHub releases are available.");
        }

        if (string.Equals(channel, SteamLoaderRuntime.UpdateChannelBeta, StringComparison.OrdinalIgnoreCase))
        {
            return SelectHighestRelease(publishedReleases.Where(release => release.Prerelease))
                ?? SelectHighestRelease(publishedReleases.Where(release => !release.Prerelease))
                ?? throw new InvalidOperationException("No compatible GitHub release is available.");
        }

        return SelectHighestRelease(publishedReleases.Where(release => !release.Prerelease))
            ?? throw new InvalidOperationException("No stable GitHub release is available.");
    }

    private static GithubRelease? SelectHighestRelease(IEnumerable<GithubRelease> releases)
    {
        GithubRelease? bestRelease = null;
        SemanticVersionParts? bestVersion = null;

        foreach (var release in releases)
        {
            var releaseVersion = ParseSemanticVersion(release.TagName);
            if (bestRelease is null)
            {
                bestRelease = release;
                bestVersion = releaseVersion;
                continue;
            }

            if (CompareReleaseCandidates(release, releaseVersion, bestRelease, bestVersion) > 0)
            {
                bestRelease = release;
                bestVersion = releaseVersion;
            }
        }

        return bestRelease;
    }

    private static int CompareReleaseCandidates(
        GithubRelease candidate,
        SemanticVersionParts? candidateVersion,
        GithubRelease currentBest,
        SemanticVersionParts? currentBestVersion)
    {
        if (candidateVersion is not null && currentBestVersion is not null)
        {
            var versionComparison = CompareSemanticVersions(candidateVersion, currentBestVersion);
            if (versionComparison != 0)
            {
                return versionComparison;
            }
        }
        else if (candidateVersion is not null || currentBestVersion is not null)
        {
            return candidateVersion is not null ? 1 : -1;
        }

        var publishedComparison = Nullable.Compare(candidate.PublishedAt, currentBest.PublishedAt);
        if (publishedComparison != 0)
        {
            return publishedComparison;
        }

        return string.Compare(
            NormalizeVersion(candidate.TagName),
            NormalizeVersion(currentBest.TagName),
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadFileAsync(
        string url,
        string destinationPath,
        Func<long?, long, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        await RetryUpdateNetworkOperationAsync(
            async () =>
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var destination = File.Create(destinationPath);

                var totalBytes = response.Content.Headers.ContentLength;
                var buffer = new byte[128 * 1024];
                long downloadedBytes = 0;
                var lastReportedPercent = -1;

                while (true)
                {
                    var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    downloadedBytes += bytesRead;

                    if (progressCallback is null)
                    {
                        continue;
                    }

                    var currentPercent = MapDownloadProgressToInstallPercent(totalBytes, downloadedBytes) ?? -1;
                    if (totalBytes.HasValue && currentPercent == lastReportedPercent)
                    {
                        continue;
                    }

                    lastReportedPercent = currentPercent;
                    await progressCallback(totalBytes, downloadedBytes);
                }

                if (progressCallback is not null)
                {
                    await progressCallback(totalBytes, downloadedBytes);
                }

                return true;
            },
            cancellationToken);
    }

    private static void ValidateDownloadedPackage(string destinationPath, bool isInstallerAsset)
    {
        if (isInstallerAsset)
        {
            ValidateInstallerPackage(destinationPath);
            return;
        }

        ValidateZipPackage(destinationPath);
    }

    private static void ValidateInstallerPackage(string destinationPath)
    {
        try
        {
            var fileInfo = new FileInfo(destinationPath);
            if (!fileInfo.Exists || fileInfo.Length < 1024)
            {
                throw new InvalidOperationException("Downloaded installer package is empty or incomplete.");
            }

            using var stream = File.OpenRead(destinationPath);
            Span<byte> header = stackalloc byte[2];
            var bytesRead = stream.Read(header);
            if (bytesRead != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            {
                throw new InvalidOperationException("Downloaded installer package is not a valid Windows executable.");
            }
        }
        catch
        {
            File.Delete(destinationPath);
            throw;
        }
    }

    private static void ValidateZipPackage(string destinationPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(destinationPath);
            if (archive.Entries.Count == 0)
            {
                throw new InvalidOperationException("Downloaded update package is empty.");
            }
        }
        catch
        {
            File.Delete(destinationPath);
            throw;
        }
    }

    private static UpdateCheckSnapshot BuildSnapshot(
        string channel,
        string currentVersion,
        GithubRelease release,
        GithubReleaseAsset? asset,
        bool updateAvailable,
        string message)
    {
        return new UpdateCheckSnapshot(
            CurrentVersion: currentVersion,
            LatestVersion: NormalizeVersion(release.TagName),
            UpdateAvailable: updateAvailable,
            CanInstall: asset is not null,
            Message: message,
            ReleaseUrl: release.HtmlUrl,
            AssetName: asset?.Name,
            PublishedAtUtc: release.PublishedAt,
            Channel: channel,
            IsPrerelease: release.Prerelease,
            ReleaseName: string.IsNullOrWhiteSpace(release.Name) ? null : release.Name.Trim(),
            CheckedAtUtc: DateTimeOffset.UtcNow,
            InstallInProgress: false,
            InstallState: null,
            InstallProgressPercent: null);
    }

    private static UpdateCheckSnapshot BuildFailureSnapshot(
        string currentVersion,
        string channel,
        string message)
    {
        return new UpdateCheckSnapshot(
            CurrentVersion: currentVersion,
            LatestVersion: null,
            UpdateAvailable: false,
            CanInstall: false,
            Message: message,
            ReleaseUrl: null,
            AssetName: null,
            PublishedAtUtc: null,
            Channel: channel,
            IsPrerelease: false,
            ReleaseName: null,
            CheckedAtUtc: DateTimeOffset.UtcNow,
            InstallInProgress: false,
            InstallState: null,
            InstallProgressPercent: null);
    }

    private static UpdateCheckSnapshot BuildInstallProgressSnapshot(
        UpdateCheckSnapshot snapshot,
        string installState,
        int? installProgressPercent,
        string message)
    {
        return snapshot with
        {
            Message = message,
            CheckedAtUtc = DateTimeOffset.UtcNow,
            InstallInProgress = true,
            InstallState = installState,
            InstallProgressPercent = installProgressPercent
        };
    }

    private static async Task ReportInstallProgressAsync(
        Func<UpdateCheckSnapshot, Task>? progressCallback,
        UpdateCheckSnapshot snapshot)
    {
        if (progressCallback is not null)
        {
            await progressCallback(snapshot);
        }
    }

    private static int? MapDownloadProgressToInstallPercent(long? totalBytes, long downloadedBytes)
    {
        if (!totalBytes.HasValue || totalBytes.Value <= 0)
        {
            return null;
        }

        var ratio = Math.Clamp(downloadedBytes / (double)totalBytes.Value, 0d, 1d);
        return 10 + (int)Math.Round(ratio * 70d);
    }

    private static string BuildDownloadProgressMessage(string assetName, long? totalBytes, long downloadedBytes)
    {
        if (totalBytes.HasValue && totalBytes.Value > 0)
        {
            return $"Downloading {assetName}... {FormatMegabytes(downloadedBytes)} / {FormatMegabytes(totalBytes.Value)}";
        }

        return $"Downloading {assetName}... {FormatMegabytes(downloadedBytes)}";
    }

    private static string FormatMegabytes(long bytes)
    {
        return $"{bytes / 1024d / 1024d:0.0} MB";
    }

    private static async Task<T> RetryUpdateNetworkOperationAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken,
        int maxAttempts = 3)
    {
        Exception? lastException = null;
        var delay = TimeSpan.FromMilliseconds(350);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < maxAttempts && IsTransientUpdateException(exception))
            {
                lastException = exception;
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2d, 2000d));
            }
            catch (Exception exception)
            {
                lastException = exception;
                break;
            }
        }

        throw lastException ?? new InvalidOperationException("The update operation did not complete successfully.");
    }

    private static bool IsTransientUpdateException(Exception exception)
    {
        return exception is HttpRequestException ||
               exception is IOException ||
               exception is TaskCanceledException ||
               exception.InnerException is HttpRequestException;
    }

    private static void TryDeleteWorkDirectory(string? workDirectory)
    {
        if (string.IsNullOrWhiteSpace(workDirectory) || !Directory.Exists(workDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(workDirectory, recursive: true);
        }
        catch
        {
        }
    }

    private static GithubReleaseAsset? FindUpdateAsset(GithubRelease release)
    {
        return release.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, SteamLoaderRuntime.ReleaseAssetName, StringComparison.OrdinalIgnoreCase))
            ?? release.Assets.FirstOrDefault(asset =>
                asset.Name.StartsWith("ToolsForSteamSetup", StringComparison.OrdinalIgnoreCase) &&
                asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInstallerAsset(GithubReleaseAsset asset)
    {
        return asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildInstallerArguments(string installDirectory)
    {
        return $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"{installDirectory}\"";
    }

    private static string BuildUpdateScript(
        string workDirectory,
        string packagePath,
        string installDirectory,
        string executableName,
        IEnumerable<int> processIdsToWaitFor,
        bool isInstallerAsset)
    {
        var processIds = string.Join(",", processIdsToWaitFor.Distinct().Where(id => id > 0));
        var installerArguments = EscapePowerShell(BuildInstallerArguments(installDirectory));
        var packageKind = isInstallerAsset ? "installer" : "portable";

        return $$"""
$ErrorActionPreference = "Stop"
$workDirectory = "{{EscapePowerShell(workDirectory)}}"
$packagePath = "{{EscapePowerShell(packagePath)}}"
$installDirectory = "{{EscapePowerShell(installDirectory)}}"
$executableName = "{{EscapePowerShell(executableName)}}"
$installerArguments = "{{installerArguments}}"
$packageKind = "{{packageKind}}"
$processIds = @({{processIds}})

foreach ($processId in $processIds) {
    try {
        Wait-Process -Id $processId -Timeout 25 -ErrorAction Stop
    } catch {
        try { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue } catch {}
    }
}

if ($packageKind -eq "installer") {
    $installer = Start-Process -FilePath $packagePath -ArgumentList $installerArguments -PassThru -Wait
    if ($installer.ExitCode -ne 0) {
        throw "The installer exited with code $($installer.ExitCode)."
    }

    Start-Sleep -Seconds 2
    Remove-Item -LiteralPath $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
    exit 0
}

$extractDirectory = Join-Path $workDirectory "package"
if (Test-Path $extractDirectory) {
    Remove-Item -LiteralPath $extractDirectory -Recurse -Force
}

Expand-Archive -LiteralPath $packagePath -DestinationPath $extractDirectory -Force
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $extractDirectory "*") -Destination $installDirectory -Recurse -Force

$exePath = Join-Path $installDirectory $executableName
Start-Process -FilePath $exePath -ArgumentList "--tray"

Start-Sleep -Seconds 2
Remove-Item -LiteralPath $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
""";
    }

    private static string EscapePowerShell(string value)
    {
        return value.Replace("`", "``", StringComparison.Ordinal).Replace("\"", "`\"", StringComparison.Ordinal);
    }

    private static string GetCurrentVersion()
    {
        return typeof(ReleaseUpdateService)
            .Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "0.0.0-dev";
    }

    private static string NormalizeVersion(string value)
    {
        var version = value.Trim();
        return version.StartsWith('v') || version.StartsWith('V')
            ? version[1..]
            : version;
    }

    private static string NormalizeUpdateChannel(string? channel)
    {
        return channel?.Trim().ToLowerInvariant() switch
        {
            SteamLoaderRuntime.UpdateChannelBeta => SteamLoaderRuntime.UpdateChannelBeta,
            _ => SteamLoaderRuntime.UpdateChannelStable
        };
    }

    private static string GetChannelLabel(string channel)
    {
        return string.Equals(channel, SteamLoaderRuntime.UpdateChannelBeta, StringComparison.OrdinalIgnoreCase)
            ? "beta"
            : "stable";
    }

    private static bool IsNewerVersion(string latestVersion, string currentVersion)
    {
        var latest = ParseSemanticVersion(latestVersion);
        var current = ParseSemanticVersion(currentVersion);

        if (latest is null || current is null)
        {
            return !string.Equals(latestVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
        }

        return CompareSemanticVersions(latest, current) > 0;
    }

    private static SemanticVersionParts? ParseSemanticVersion(string value)
    {
        var normalized = NormalizeVersion(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var metadataSeparatorIndex = normalized.IndexOf('+', StringComparison.Ordinal);
        if (metadataSeparatorIndex >= 0)
        {
            normalized = normalized[..metadataSeparatorIndex];
        }

        string[] prereleaseIdentifiers = [];
        var prereleaseSeparatorIndex = normalized.IndexOf('-', StringComparison.Ordinal);
        if (prereleaseSeparatorIndex >= 0)
        {
            var prereleaseSuffix = normalized[(prereleaseSeparatorIndex + 1)..];
            prereleaseIdentifiers = prereleaseSuffix
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            normalized = normalized[..prereleaseSeparatorIndex];
        }

        var numberTokens = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (numberTokens.Length == 0)
        {
            return null;
        }

        var numbers = new int[numberTokens.Length];
        for (var index = 0; index < numberTokens.Length; index += 1)
        {
            if (!int.TryParse(numberTokens[index], out numbers[index]))
            {
                return null;
            }
        }

        return new SemanticVersionParts(numbers, prereleaseIdentifiers);
    }

    private static int CompareSemanticVersions(SemanticVersionParts left, SemanticVersionParts right)
    {
        var maxNumberCount = Math.Max(left.Numbers.Length, right.Numbers.Length);
        for (var index = 0; index < maxNumberCount; index += 1)
        {
            var leftNumber = index < left.Numbers.Length ? left.Numbers[index] : 0;
            var rightNumber = index < right.Numbers.Length ? right.Numbers[index] : 0;
            var numberComparison = leftNumber.CompareTo(rightNumber);
            if (numberComparison != 0)
            {
                return numberComparison;
            }
        }

        var leftHasPrerelease = left.PrereleaseIdentifiers.Length > 0;
        var rightHasPrerelease = right.PrereleaseIdentifiers.Length > 0;
        if (leftHasPrerelease != rightHasPrerelease)
        {
            return leftHasPrerelease ? -1 : 1;
        }

        var maxIdentifierCount = Math.Max(left.PrereleaseIdentifiers.Length, right.PrereleaseIdentifiers.Length);
        for (var index = 0; index < maxIdentifierCount; index += 1)
        {
            if (index >= left.PrereleaseIdentifiers.Length)
            {
                return -1;
            }

            if (index >= right.PrereleaseIdentifiers.Length)
            {
                return 1;
            }

            var leftIdentifier = left.PrereleaseIdentifiers[index];
            var rightIdentifier = right.PrereleaseIdentifiers[index];
            var leftIsNumeric = int.TryParse(leftIdentifier, out var leftNumericIdentifier);
            var rightIsNumeric = int.TryParse(rightIdentifier, out var rightNumericIdentifier);

            if (leftIsNumeric && rightIsNumeric)
            {
                var numericComparison = leftNumericIdentifier.CompareTo(rightNumericIdentifier);
                if (numericComparison != 0)
                {
                    return numericComparison;
                }

                continue;
            }

            if (leftIsNumeric != rightIsNumeric)
            {
                return leftIsNumeric ? -1 : 1;
            }

            var textComparison = string.Compare(leftIdentifier, rightIdentifier, StringComparison.OrdinalIgnoreCase);
            if (textComparison != 0)
            {
                return textComparison;
            }
        }

        return 0;
    }

    private sealed record SemanticVersionParts(int[] Numbers, string[] PrereleaseIdentifiers);

    private sealed record GithubRelease(
        [property: JsonPropertyName("tag_name")]
        string TagName,
        [property: JsonPropertyName("name")]
        string? Name,
        [property: JsonPropertyName("html_url")]
        string HtmlUrl,
        [property: JsonPropertyName("published_at")]
        DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("draft")]
        bool Draft,
        [property: JsonPropertyName("prerelease")]
        bool Prerelease,
        [property: JsonPropertyName("assets")]
        IReadOnlyList<GithubReleaseAsset> Assets);

    private sealed record GithubReleaseAsset(
        [property: JsonPropertyName("name")]
        string Name,
        [property: JsonPropertyName("browser_download_url")]
        string BrowserDownloadUrl);
}
