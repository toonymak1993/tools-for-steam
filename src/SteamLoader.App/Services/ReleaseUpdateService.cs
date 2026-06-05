using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamLoader.App.Models;

namespace SteamLoader.App.Services;

public sealed class ReleaseUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Uri LatestReleaseUri = new(
        $"https://api.github.com/repos/{SteamLoaderRuntime.ReleaseRepository}/releases/latest");

    private readonly HttpClient _httpClient;

    public ReleaseUpdateService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ToolsForSteam-Updater/1.0");
    }

    public async Task<UpdateCheckSnapshot> CheckAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentVersion();

        try
        {
            var release = await GetLatestReleaseAsync(cancellationToken);
            var latestVersion = NormalizeVersion(release.TagName);
            var asset = FindUpdateAsset(release);
            var updateAvailable = IsNewerVersion(latestVersion, currentVersion);

            return new UpdateCheckSnapshot(
                CurrentVersion: currentVersion,
                LatestVersion: latestVersion,
                UpdateAvailable: updateAvailable,
                CanInstall: updateAvailable && asset is not null,
                Message: updateAvailable
                    ? asset is null
                        ? $"Version {latestVersion} is available, but no Windows package was attached to the release."
                        : $"Version {latestVersion} is available."
                    : $"You are on the latest release ({currentVersion}).",
                ReleaseUrl: release.HtmlUrl,
                AssetName: asset?.Name,
                PublishedAtUtc: release.PublishedAt);
        }
        catch (Exception exception)
        {
            return new UpdateCheckSnapshot(
                CurrentVersion: currentVersion,
                LatestVersion: null,
                UpdateAvailable: false,
                CanInstall: false,
                Message: $"Update check failed: {exception.Message}",
                ReleaseUrl: null,
                AssetName: null,
                PublishedAtUtc: null);
        }
    }

    public async Task<UpdateCheckSnapshot> BeginInstallLatestAsync(
        string installDirectory,
        string executablePath,
        IEnumerable<int> processIdsToWaitFor,
        CancellationToken cancellationToken = default)
    {
        var release = await GetLatestReleaseAsync(cancellationToken);
        var latestVersion = NormalizeVersion(release.TagName);
        var asset = FindUpdateAsset(release)
            ?? throw new InvalidOperationException("The latest release does not contain a Tools for Steam Windows package.");

        var workDirectory = Path.Combine(Path.GetTempPath(), $"ToolsForSteam-Update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);

        var packagePath = Path.Combine(workDirectory, asset.Name);
        await DownloadFileAsync(asset.BrowserDownloadUrl, packagePath, cancellationToken);

        if (IsInstallerAsset(asset))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = packagePath,
                Arguments = BuildInstallerArguments(installDirectory),
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            })?.Dispose();

            return new UpdateCheckSnapshot(
                CurrentVersion: GetCurrentVersion(),
                LatestVersion: latestVersion,
                UpdateAvailable: true,
                CanInstall: true,
                Message: $"Installing {latestVersion}. Tools for Steam will restart when setup is finished.",
                ReleaseUrl: release.HtmlUrl,
                AssetName: asset.Name,
                PublishedAtUtc: release.PublishedAt);
        }

        ValidateZipPackage(packagePath);

        var scriptPath = Path.Combine(workDirectory, "apply-update.ps1");
        File.WriteAllText(
            scriptPath,
            BuildUpdateScript(
                workDirectory,
                packagePath,
                installDirectory,
                Path.GetFileName(executablePath),
                processIdsToWaitFor));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        })?.Dispose();

        return new UpdateCheckSnapshot(
            CurrentVersion: GetCurrentVersion(),
            LatestVersion: latestVersion,
            UpdateAvailable: true,
            CanInstall: true,
            Message: $"Installing {latestVersion}. Tools for Steam will restart when the update is applied.",
            ReleaseUrl: release.HtmlUrl,
            AssetName: asset.Name,
            PublishedAtUtc: release.PublishedAt);
    }

    private async Task<GithubRelease> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        await using var stream = await _httpClient.GetStreamAsync(LatestReleaseUri, cancellationToken);
        return await JsonSerializer.DeserializeAsync<GithubRelease>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty release response.");
    }

    private async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken);
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

    private static GithubReleaseAsset? FindUpdateAsset(GithubRelease release)
    {
        return release.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, SteamLoaderRuntime.ReleaseAssetName, StringComparison.OrdinalIgnoreCase))
            ?? release.Assets.FirstOrDefault(asset =>
                asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                asset.Name.Contains("ToolsForSteam", StringComparison.OrdinalIgnoreCase) &&
                asset.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase));
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
        string zipPath,
        string installDirectory,
        string executableName,
        IEnumerable<int> processIdsToWaitFor)
    {
        var processIds = string.Join(",", processIdsToWaitFor.Distinct().Where(id => id > 0));
        return $$"""
$ErrorActionPreference = "Stop"
$workDirectory = "{{EscapePowerShell(workDirectory)}}"
$zipPath = "{{EscapePowerShell(zipPath)}}"
$installDirectory = "{{EscapePowerShell(installDirectory)}}"
$executableName = "{{EscapePowerShell(executableName)}}"
$processIds = @({{processIds}})

foreach ($processId in $processIds) {
    try {
        Wait-Process -Id $processId -Timeout 25 -ErrorAction Stop
    } catch {
        try { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue } catch {}
    }
}

$extractDirectory = Join-Path $workDirectory "package"
if (Test-Path $extractDirectory) {
    Remove-Item -LiteralPath $extractDirectory -Recurse -Force
}

Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDirectory -Force
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

    private static bool IsNewerVersion(string latestVersion, string currentVersion)
    {
        var latest = ParseVersion(latestVersion);
        var current = ParseVersion(currentVersion);

        if (latest is null || current is null)
        {
            return !string.Equals(latestVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
        }

        return latest > current;
    }

    private static Version? ParseVersion(string value)
    {
        var normalized = NormalizeVersion(value);
        var dashIndex = normalized.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex >= 0)
        {
            normalized = normalized[..dashIndex];
        }

        return Version.TryParse(normalized, out var version) ? version : null;
    }

    private sealed record GithubRelease(
        [property: JsonPropertyName("tag_name")]
        string TagName,
        [property: JsonPropertyName("html_url")]
        string HtmlUrl,
        [property: JsonPropertyName("published_at")]
        DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("assets")]
        IReadOnlyList<GithubReleaseAsset> Assets);

    private sealed record GithubReleaseAsset(
        [property: JsonPropertyName("name")]
        string Name,
        [property: JsonPropertyName("browser_download_url")]
        string BrowserDownloadUrl);
}
