using System.ComponentModel;
using System.Diagnostics;
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
        var nvidiaGpuDetected = DetectNvidiaGpu();
        var installed = File.Exists(ToolPath) && HasExpectedHash(ToolPath);
        return new NvidiaDriverUpdateSnapshot(
            ToolName,
            Version,
            nvidiaGpuDetected,
            installed,
            IsRunning(),
            statusText ?? (nvidiaGpuDetected
                ? installed
                    ? "GPU update is ready."
                    : "The official GPU update tool will be downloaded on first use."
                : "No NVIDIA GPU was detected."),
            ProjectUrl,
            "GPL-3.0");
    }

    public async Task<NvidiaDriverUpdateSnapshot> LaunchAsync(CancellationToken cancellationToken)
    {
        await _installGate.WaitAsync(cancellationToken);
        try
        {
            if (!DetectNvidiaGpu())
            {
                throw new InvalidOperationException(
                    "GPU Update is available only when an NVIDIA GPU is detected.");
            }

            if (IsRunning())
            {
                return GetSnapshot("TinyNvidiaUpdateChecker is already running.");
            }

            var toolPath = await EnsureInstalledAsync(cancellationToken);
            Directory.CreateDirectory(ToolDirectory);

            using var process = Process.Start(CreateLaunchStartInfo(toolPath, ConfigPath))
                ?? throw new InvalidOperationException("TinyNvidiaUpdateChecker could not be started.");

            return GetSnapshot("TinyNvidiaUpdateChecker was started.");
        }
        finally
        {
            _installGate.Release();
        }
    }

    public async Task<NvidiaDriverUpdateSnapshot> LaunchSilentGameReadyAsync(
        CancellationToken cancellationToken)
    {
        await _installGate.WaitAsync(cancellationToken);
        try
        {
            if (!DetectNvidiaGpu())
            {
                throw new InvalidOperationException(
                    "Silent Game Ready Update is available only when an NVIDIA GPU is detected.");
            }

            if (IsRunning())
            {
                return GetSnapshot("TinyNvidiaUpdateChecker is already running.");
            }

            var toolPath = await EnsureInstalledAsync(cancellationToken);
            Directory.CreateDirectory(ToolDirectory);
            await EnsureSilentGameReadyConfigAsync(
                SilentGameReadyConfigPath,
                cancellationToken);

            try
            {
                using var process = Process.Start(
                    CreateSilentGameReadyStartInfo(toolPath, SilentGameReadyConfigPath))
                    ?? throw new InvalidOperationException(
                        "The silent Game Ready driver update could not be started.");
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                throw new InvalidOperationException(
                    "Windows administrator approval was cancelled. No driver update was started.",
                    exception);
            }

            return GetSnapshot(
                "Silent Game Ready update started. If an update is available, it will be installed without a restart.");
        }
        finally
        {
            _installGate.Release();
        }
    }

    internal static bool IsNvidiaAdapter(
        string? name,
        string? adapterCompatibility,
        string? pnpDeviceId)
    {
        return new[] { name, adapterCompatibility }
                   .Any(value => value?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true) ||
               pnpDeviceId?.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static ProcessStartInfo CreateLaunchStartInfo(string toolPath, string configPath)
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
        startInfo.ArgumentList.Add("--quiet");
        startInfo.ArgumentList.Add("--noprompt");
        startInfo.ArgumentList.Add("--confirm-dl");
        startInfo.ArgumentList.Add($"--config-override={configPath}");
        return startInfo;
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
            return actualHash.Equals(Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> EnsureInstalledAsync(CancellationToken cancellationToken)
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

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
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
}

public sealed record NvidiaDriverUpdateSnapshot(
    string ToolName,
    string Version,
    bool NvidiaGpuDetected,
    bool Installed,
    bool Running,
    string StatusText,
    string ProjectUrl,
    string License);
