using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace SteamLoader.App.Infrastructure.SystemTools;

public sealed class NvidiaDriverUpdateService
{
    internal const string ToolName = "TinyNvidiaUpdateChecker";
    internal const string Version = "1.25.2";
    internal const string DownloadUrl =
        "https://github.com/HawaiiBeach/TinyNvidiaUpdateChecker/releases/download/v1.25.2/TinyNvidiaUpdateChecker.exe";
    internal const string Sha256 =
        "69760c6933cd9ad30bbdf60dd55b262d0a625239ab4c7db424dd7a2a793692ec";
    internal const string ProjectUrl =
        "https://github.com/HawaiiBeach/TinyNvidiaUpdateChecker";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly object _runtimeStateGate = new();
    private readonly bool _nvidiaGpuDetected = DetectNvidiaGpu();
    private NvidiaDriverRuntimeState _runtimeState = NvidiaDriverRuntimeState.Idle;

    public string ToolDirectory =>
        Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "system",
            "driver",
            "tiny-nvidia-update-checker");

    public string ToolPath => Path.Combine(ToolDirectory, "TinyNvidiaUpdateChecker.exe");

    public string ConfigPath => Path.Combine(ToolDirectory, "app.config");

    public string SilentGameReadyConfigPath =>
        Path.Combine(ToolDirectory, "silent-game-ready.config");

    public NvidiaDriverUpdateSnapshot GetSnapshot(string? statusText = null)
    {
        var runtimeState = GetRuntimeState();
        var installed = File.Exists(ToolPath) && HasExpectedHash(ToolPath);
        return new NvidiaDriverUpdateSnapshot(
            ToolName,
            Version,
            _nvidiaGpuDetected,
            installed,
            runtimeState.Busy || IsRunning(),
            statusText ?? runtimeState.StatusText ?? (_nvidiaGpuDetected
                ? installed
                    ? "Ready to check for an NVIDIA Game Ready driver."
                    : "The verified NVIDIA update helper will be downloaded on first use."
                : "No NVIDIA GPU was detected."),
            ProjectUrl,
            "GPL-3.0",
            runtimeState.Phase,
            runtimeState.Busy,
            runtimeState.InstalledDriverVersion,
            runtimeState.AvailableDriverVersion,
            runtimeState.UpdateAvailable,
            runtimeState.SteamRestartRequired,
            runtimeState.DownloadedBytes,
            runtimeState.TotalDownloadBytes,
            runtimeState.DownloadProgressPercent,
            runtimeState.DownloadBytesPerSecond,
            runtimeState.LastCheckedAt,
            runtimeState.ErrorText);
    }

    public async Task<NvidiaDriverUpdateSnapshot> CheckGameReadyAsync(
        CancellationToken cancellationToken)
    {
        await _installGate.WaitAsync(cancellationToken);
        try
        {
            EnsureNvidiaGpuDetected();
            var currentState = GetRuntimeState();
            if (currentState.Busy || IsRunning())
            {
                throw new InvalidOperationException(
                    "The NVIDIA driver workflow is already running.");
            }

            var steamRestartRequired = currentState.SteamRestartRequired;
            SetRuntimeState(
                NvidiaDriverRuntimeState.Idle with
                {
                    Phase = "checking",
                    Busy = true,
                    SteamRestartRequired = steamRestartRequired,
                    StatusText = "Checking NVIDIA for a newer Game Ready driver..."
                });

            try
            {
                var toolPath = await EnsureInstalledAsync(cancellationToken);
                Directory.CreateDirectory(ToolDirectory);
                await EnsureSilentGameReadyConfigAsync(
                    SilentGameReadyConfigPath,
                    cancellationToken);

                var result = await RunGameReadyCheckAsync(
                    toolPath,
                    SilentGameReadyConfigPath,
                    cancellationToken);
                var totalDownloadBytes = result.DownloadUrl is null
                    ? result.EstimatedDownloadBytes
                    : await TryGetDownloadSizeAsync(
                        result.DownloadUrl,
                        result.EstimatedDownloadBytes,
                        cancellationToken);
                var statusText = result.UpdateAvailable
                    ? $"Game Ready {result.AvailableDriverVersion} is available. Installed: {result.InstalledDriverVersion}."
                    : $"Game Ready {result.InstalledDriverVersion} is already up to date.";

                SetRuntimeState(
                    new NvidiaDriverRuntimeState(
                        result.UpdateAvailable ? "available" : "up-to-date",
                        Busy: false,
                        result.InstalledDriverVersion,
                        result.AvailableDriverVersion,
                        result.UpdateAvailable,
                        steamRestartRequired,
                        DownloadedBytes: 0,
                        totalDownloadBytes,
                        DownloadProgressPercent: null,
                        DownloadBytesPerSecond: null,
                        LastCheckedAt: DateTimeOffset.UtcNow,
                        ErrorText: null,
                        statusText,
                        result.DownloadUrl));
                return GetSnapshot();
            }
            catch (OperationCanceledException)
            {
                SetRuntimeFailure("The NVIDIA Game Ready check was cancelled.");
                throw;
            }
            catch (Exception exception)
            {
                SetRuntimeFailure(
                    $"The NVIDIA Game Ready check failed: {exception.Message}");
                throw new InvalidOperationException(
                    $"The NVIDIA Game Ready check failed: {exception.Message}",
                    exception);
            }
        }
        finally
        {
            _installGate.Release();
        }
    }

    public async Task<NvidiaDriverUpdateSnapshot> StartGameReadyInstallAsync(
        CancellationToken cancellationToken)
    {
        await _installGate.WaitAsync(cancellationToken);
        try
        {
            EnsureNvidiaGpuDetected();
            var runtimeState = GetRuntimeState();
            if (runtimeState.Busy || IsRunning())
            {
                throw new InvalidOperationException(
                    "The NVIDIA driver workflow is already running.");
            }

            if (runtimeState.UpdateAvailable != true ||
                string.IsNullOrWhiteSpace(runtimeState.AvailableDriverVersion) ||
                string.IsNullOrWhiteSpace(runtimeState.DownloadUrl))
            {
                throw new InvalidOperationException(
                    "Check for a newer Game Ready driver before starting the installation.");
            }

            var toolPath = await EnsureInstalledAsync(cancellationToken);
            Directory.CreateDirectory(ToolDirectory);
            await EnsureSilentGameReadyConfigAsync(
                SilentGameReadyConfigPath,
                cancellationToken);

            var installState = runtimeState with
            {
                Phase = "preparing",
                Busy = true,
                DownloadedBytes = 0,
                DownloadProgressPercent = 0,
                DownloadBytesPerSecond = null,
                ErrorText = null,
                StatusText =
                    $"Preparing Game Ready {runtimeState.AvailableDriverVersion}..."
            };
            SetRuntimeState(installState);

            Process process;
            try
            {
                process = Process.Start(
                    CreateTrackedSilentGameReadyStartInfo(
                        toolPath,
                        SilentGameReadyConfigPath))
                    ?? throw new InvalidOperationException(
                        "The Game Ready installation could not be started.");
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                SetRuntimeFailure(
                    "Windows administrator approval was cancelled. No driver update was installed.");
                throw new InvalidOperationException(
                    "Windows administrator approval was cancelled. No driver update was installed.",
                    exception);
            }
            catch (Exception exception)
            {
                SetRuntimeFailure(
                    $"The Game Ready installation could not be started: {exception.Message}");
                throw;
            }

            _ = TrackGameReadyInstallAsync(process, installState);
            return GetSnapshot();
        }
        finally
        {
            _installGate.Release();
        }
    }

    public async Task<NvidiaDriverUpdateSnapshot> LaunchAsync(
        CancellationToken cancellationToken)
    {
        await _installGate.WaitAsync(cancellationToken);
        try
        {
            EnsureNvidiaGpuDetected();
            if (GetRuntimeState().Busy || IsRunning())
            {
                return GetSnapshot("TinyNvidiaUpdateChecker is already running.");
            }

            var toolPath = await EnsureInstalledAsync(cancellationToken);
            Directory.CreateDirectory(ToolDirectory);

            using var process = Process.Start(CreateLaunchStartInfo(toolPath, ConfigPath))
                ?? throw new InvalidOperationException(
                    "TinyNvidiaUpdateChecker could not be started.");

            return GetSnapshot("TinyNvidiaUpdateChecker was started.");
        }
        finally
        {
            _installGate.Release();
        }
    }

    public Task<NvidiaDriverUpdateSnapshot> LaunchSilentGameReadyAsync(
        CancellationToken cancellationToken)
    {
        return StartGameReadyInstallAsync(cancellationToken);
    }

    public NvidiaDriverUpdateSnapshot AcknowledgeSteamRestart()
    {
        var runtimeState = GetRuntimeState();
        if (!runtimeState.SteamRestartRequired)
        {
            throw new InvalidOperationException(
                "Steam does not need to be restarted for a completed NVIDIA driver update.");
        }

        SetRuntimeState(
            runtimeState with
            {
                SteamRestartRequired = false,
                StatusText =
                    "Steam is restarting so Big Picture and its GPU processes reload the updated driver."
            });
        return GetSnapshot();
    }

    internal static bool IsNvidiaAdapter(
        string? name,
        string? adapterCompatibility,
        string? pnpDeviceId)
    {
        return new[] { name, adapterCompatibility }
                   .Any(value =>
                       value?.Contains(
                           "NVIDIA",
                           StringComparison.OrdinalIgnoreCase) == true) ||
               pnpDeviceId?.Contains(
                   "VEN_10DE",
                   StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static ProcessStartInfo CreateLaunchStartInfo(
        string toolPath,
        string configPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = toolPath,
            WorkingDirectory = Path.GetDirectoryName(toolPath) ?? AppContext.BaseDirectory,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add($"--config-override={configPath}");
        return startInfo;
    }

    internal static ProcessStartInfo CreateGameReadyCheckStartInfo(
        string toolPath,
        string configPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = toolPath,
            WorkingDirectory = Path.GetDirectoryName(toolPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--dry-run");
        startInfo.ArgumentList.Add("--debug");
        startInfo.ArgumentList.Add("--noprompt");
        startInfo.ArgumentList.Add($"--config-override={configPath}");
        return startInfo;
    }

    internal static ProcessStartInfo CreateSilentGameReadyStartInfo(
        string toolPath,
        string configPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = toolPath,
            WorkingDirectory = Path.GetDirectoryName(toolPath) ?? AppContext.BaseDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        AddSilentGameReadyArguments(startInfo, configPath);
        return startInfo;
    }

    internal static ProcessStartInfo CreateTrackedSilentGameReadyStartInfo(
        string toolPath,
        string configPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = toolPath,
            WorkingDirectory = Path.GetDirectoryName(toolPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        AddSilentGameReadyArguments(startInfo, configPath);
        return startInfo;
    }

    internal static NvidiaDriverCheckResult ParseGameReadyCheckOutput(string output)
    {
        var installedVersion = ReadOutputValue(output, "OfflineGPUVersion");
        var availableVersion = ReadOutputValue(output, "OnlineGPUVersion");
        var downloadUrl = ReadOutputValue(output, "downloadURL");
        var downloadSizeText = ReadOutputValue(output, "downloadFileSize");

        if (string.IsNullOrWhiteSpace(installedVersion) ||
            string.IsNullOrWhiteSpace(availableVersion))
        {
            throw new InvalidOperationException(
                "TinyNvidiaUpdateChecker did not return NVIDIA driver versions.");
        }

        if (!System.Version.TryParse(installedVersion, out var installed) ||
            !System.Version.TryParse(availableVersion, out var available))
        {
            throw new InvalidOperationException(
                "TinyNvidiaUpdateChecker returned an invalid NVIDIA driver version.");
        }

        long? estimatedDownloadBytes = null;
        if (!string.IsNullOrWhiteSpace(downloadSizeText))
        {
            var numericText = downloadSizeText
                .Replace("MiB", "", StringComparison.OrdinalIgnoreCase)
                .Trim();
            if (double.TryParse(
                    numericText,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var mebibytes))
            {
                estimatedDownloadBytes = (long)Math.Round(
                    mebibytes * 1024d * 1024d);
            }
        }

        return new NvidiaDriverCheckResult(
            installedVersion,
            availableVersion,
            available > installed,
            Uri.TryCreate(downloadUrl, UriKind.Absolute, out var parsedUri)
                ? parsedUri.ToString()
                : null,
            estimatedDownloadBytes);
    }

    internal static string CreateSilentGameReadyConfig()
    {
        return """
               <?xml version="1.0" encoding="utf-8"?>
               <configuration>
                 <appSettings>
                   <add key="Check for Updates" value="false" />
                   <add key="Minimal install" value="false" />
                   <add key="Driver type" value="grd" />
                 </appSettings>
               </configuration>
               """;
    }

    internal static bool HasExpectedHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            return actualHash.Equals(
                Sha256,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void AddSilentGameReadyArguments(
        ProcessStartInfo startInfo,
        string configPath)
    {
        startInfo.ArgumentList.Add("--quiet");
        startInfo.ArgumentList.Add("--noprompt");
        startInfo.ArgumentList.Add("--confirm-dl");
        startInfo.ArgumentList.Add($"--config-override={configPath}");
    }

    private async Task<NvidiaDriverCheckResult> RunGameReadyCheckAsync(
        string toolPath,
        string configPath,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(
            CreateGameReadyCheckStartInfo(toolPath, configPath))
            ?? throw new InvalidOperationException(
                "The NVIDIA Game Ready check could not be started.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(standardError)
                    ? $"TinyNvidiaUpdateChecker exited with code {process.ExitCode}."
                    : standardError.Trim());
        }

        return ParseGameReadyCheckOutput(standardOutput);
    }

    private async Task TrackGameReadyInstallAsync(
        Process process,
        NvidiaDriverRuntimeState installState)
    {
        var downloadPath = GetDriverDownloadPath(
            installState.AvailableDriverVersion,
            installState.DownloadUrl);
        var previousBytes = 0L;
        var previousSampleAt = DateTimeOffset.UtcNow;
        var startedAt = previousSampleAt;
        var installingReached = false;

        try
        {
            while (!process.HasExited)
            {
                var now = DateTimeOffset.UtcNow;
                var downloadedBytes = ReadFileLength(downloadPath);
                var totalBytes = installState.TotalDownloadBytes;
                var elapsedSeconds = Math.Max(
                    0.001,
                    (now - previousSampleAt).TotalSeconds);
                long? bytesPerSecond = downloadedBytes > previousBytes
                    ? (long)Math.Round(
                        (downloadedBytes - previousBytes) / elapsedSeconds)
                    : null;
                int? progressPercent = totalBytes > 0
                    ? Math.Clamp(
                        (int)Math.Floor(downloadedBytes * 100d / totalBytes.Value),
                        0,
                        100)
                    : null;

                if (downloadedBytes > 0 &&
                    totalBytes > 0 &&
                    downloadedBytes >= totalBytes.Value)
                {
                    installingReached = true;
                }

                var phase = installingReached
                    ? "installing"
                    : downloadedBytes > 0 ||
                      now - startedAt > TimeSpan.FromSeconds(2)
                        ? "downloading"
                        : "preparing";
                var statusText = phase switch
                {
                    "installing" =>
                        "Installing the NVIDIA Game Ready driver. Windows may request administrator approval.",
                    "downloading" when progressPercent is not null =>
                        $"Downloading NVIDIA Game Ready {installState.AvailableDriverVersion}: {progressPercent}%.",
                    "downloading" =>
                        $"Downloading NVIDIA Game Ready {installState.AvailableDriverVersion}...",
                    _ =>
                        $"Preparing NVIDIA Game Ready {installState.AvailableDriverVersion}..."
                };

                SetRuntimeState(
                    installState with
                    {
                        Phase = phase,
                        Busy = true,
                        DownloadedBytes = downloadedBytes,
                        DownloadProgressPercent = installingReached
                            ? null
                            : progressPercent,
                        DownloadBytesPerSecond = installingReached
                            ? null
                            : bytesPerSecond,
                        StatusText = statusText
                    });

                previousBytes = downloadedBytes;
                previousSampleAt = now;
                await Task.Delay(750);
            }

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                SetRuntimeFailure(
                    $"The NVIDIA driver installer exited with code {process.ExitCode}.");
                return;
            }

            SetRuntimeState(
                installState with
                {
                    Phase = "completed",
                    Busy = false,
                    InstalledDriverVersion =
                        installState.AvailableDriverVersion,
                    UpdateAvailable = false,
                    SteamRestartRequired = true,
                    DownloadedBytes =
                        installState.TotalDownloadBytes ??
                        Math.Max(previousBytes, 0),
                    DownloadProgressPercent = 100,
                    DownloadBytesPerSecond = null,
                    ErrorText = null,
                    StatusText =
                        "The NVIDIA Game Ready installation finished. Restart Steam to reload its GPU processes. A Windows restart may still be required."
                });
        }
        catch (Exception exception)
        {
            SetRuntimeFailure(
                $"The NVIDIA Game Ready installation failed: {exception.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task<string> EnsureInstalledAsync(
        CancellationToken cancellationToken)
    {
        if (File.Exists(ToolPath) && HasExpectedHash(ToolPath))
        {
            return ToolPath;
        }

        Directory.CreateDirectory(ToolDirectory);
        var temporaryPath = Path.Combine(
            ToolDirectory,
            $"TinyNvidiaUpdateChecker-{Version}-{Guid.NewGuid():N}.tmp");

        try
        {
            using var response = await HttpClient.GetAsync(
                DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var source =
                         await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }

            if (!HasExpectedHash(temporaryPath))
            {
                throw new InvalidOperationException(
                    "The downloaded GPU update tool failed its integrity check.");
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

    private static async Task EnsureSilentGameReadyConfigAsync(
        string configPath,
        CancellationToken cancellationToken)
    {
        var expectedConfig = CreateSilentGameReadyConfig();
        if (File.Exists(configPath) &&
            string.Equals(
                await File.ReadAllTextAsync(configPath, cancellationToken),
                expectedConfig,
                StringComparison.Ordinal))
        {
            return;
        }

        await File.WriteAllTextAsync(
            configPath,
            expectedConfig,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private static async Task<long?> TryGetDownloadSizeAsync(
        string downloadUrl,
        long? fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Head,
                downloadUrl);
            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return response.IsSuccessStatusCode
                ? response.Content.Headers.ContentLength ?? fallback
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string? GetDriverDownloadPath(
        string? availableDriverVersion,
        string? downloadUrl)
    {
        if (string.IsNullOrWhiteSpace(availableDriverVersion) ||
            !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var fileName = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(fileName)
            ? null
            : Path.Combine(
                Path.GetTempPath(),
                availableDriverVersion,
                fileName);
    }

    private static long ReadFileLength(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
                ? 0
                : new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string? ReadOutputValue(string output, string key)
    {
        foreach (var line in output.Split(
                     ["\r\n", "\n"],
                     StringSplitOptions.None))
        {
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0 ||
                !line[..separatorIndex]
                    .Trim()
                    .Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[(separatorIndex + 1)..].Trim();
        }

        return null;
    }

    private NvidiaDriverRuntimeState GetRuntimeState()
    {
        lock (_runtimeStateGate)
        {
            return _runtimeState;
        }
    }

    private void SetRuntimeState(NvidiaDriverRuntimeState runtimeState)
    {
        lock (_runtimeStateGate)
        {
            _runtimeState = runtimeState;
        }
    }

    private void SetRuntimeFailure(string message)
    {
        var current = GetRuntimeState();
        SetRuntimeState(
            current with
            {
                Phase = "error",
                Busy = false,
                DownloadBytesPerSecond = null,
                ErrorText = message,
                StatusText = message
            });
    }

    private void EnsureNvidiaGpuDetected()
    {
        if (!_nvidiaGpuDetected)
        {
            throw new InvalidOperationException(
                "NVIDIA Game Ready updates are available only when an NVIDIA GPU is detected.");
        }
    }

    private static bool IsRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(ToolName);
            try
            {
                return processes.Any(process => !process.HasExited);
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool DetectNvidiaGpu()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterCompatibility, PNPDeviceID FROM Win32_VideoController");
            using var results = searcher.Get();
            foreach (ManagementObject adapter in results)
            {
                if (IsNvidiaAdapter(
                        Convert.ToString(adapter["Name"]),
                        Convert.ToString(adapter["AdapterCompatibility"]),
                        Convert.ToString(adapter["PNPDeviceID"])))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private sealed record NvidiaDriverRuntimeState(
        string Phase,
        bool Busy,
        string? InstalledDriverVersion,
        string? AvailableDriverVersion,
        bool? UpdateAvailable,
        bool SteamRestartRequired,
        long DownloadedBytes,
        long? TotalDownloadBytes,
        int? DownloadProgressPercent,
        long? DownloadBytesPerSecond,
        DateTimeOffset? LastCheckedAt,
        string? ErrorText,
        string? StatusText,
        string? DownloadUrl)
    {
        public static NvidiaDriverRuntimeState Idle { get; } =
            new(
                "idle",
                Busy: false,
                InstalledDriverVersion: null,
                AvailableDriverVersion: null,
                UpdateAvailable: null,
                SteamRestartRequired: false,
                DownloadedBytes: 0,
                TotalDownloadBytes: null,
                DownloadProgressPercent: null,
                DownloadBytesPerSecond: null,
                LastCheckedAt: null,
                ErrorText: null,
                StatusText: null,
                DownloadUrl: null);
    }
}

internal sealed record NvidiaDriverCheckResult(
    string InstalledDriverVersion,
    string AvailableDriverVersion,
    bool UpdateAvailable,
    string? DownloadUrl,
    long? EstimatedDownloadBytes);

public sealed record NvidiaDriverUpdateSnapshot(
    string ToolName,
    string Version,
    bool NvidiaGpuDetected,
    bool Installed,
    bool Running,
    string StatusText,
    string ProjectUrl,
    string License,
    string Phase,
    bool Busy,
    string? InstalledDriverVersion,
    string? AvailableDriverVersion,
    bool? UpdateAvailable,
    bool SteamRestartRequired,
    long DownloadedBytes,
    long? TotalDownloadBytes,
    int? DownloadProgressPercent,
    long? DownloadBytesPerSecond,
    DateTimeOffset? LastCheckedAt,
    string? ErrorText);
