using System.Diagnostics;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.Performance;

public sealed class TfsPerformanceService : IDisposable
{
    private static readonly PerformanceOverlayLevelState[] OverlayLevels =
    [
        new(0, "Off", "The Tools for Steam RTSS overlay is disabled.", false),
        new(1, "FPS", "One large, distraction-free FPS value.", false),
        new(2, "SteamOS Strip", "FPS, frametime, 1% low, CPU, RAM, battery, and frame cap in one line.", false),
        new(3, "SteamOS Full", "Detailed MangoHud-style panel with system values and a live frametime graph.", false),
        new(4, "Frame Pacing", "Focused FPS, 1% low, frametime, and a large live frametime graph.", false)
    ];

    private readonly PerformanceSettingsStore _settingsStore;
    private readonly RtssInstallationService _installationService = new();
    private readonly RtssSharedMemoryClient _sharedMemory = new();
    private readonly object _gate = new();
    private readonly System.Threading.Timer _updateTimer;
    private PerformanceRuntimeStatus _runtimeStatus = new();
    private int _lastProfileProcessId;
    private int _lastProfileFingerprint;
    private int _cpuProcessId;
    private TimeSpan _lastCpuTime;
    private DateTimeOffset _lastCpuSampleUtc;
    private bool _disposed;

    public TfsPerformanceService(PerformanceSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        var configuration = _settingsStore.Load();
        var pollingEnabled = configuration.OverlayEnabled || configuration.FrameLimitConfigured;
        _updateTimer = new System.Threading.Timer(
            _ => UpdateOverlay(),
            null,
            pollingEnabled ? TimeSpan.FromMilliseconds(250) : Timeout.InfiniteTimeSpan,
            pollingEnabled ? TimeSpan.FromMilliseconds(250) : Timeout.InfiniteTimeSpan);
    }

    public PerformanceSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return BuildSnapshot(_settingsStore.Load());
        }
    }

    public PerformanceSnapshot SetOverlayLevel(int level)
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            configuration.OverlayLevel = Math.Clamp(level, 0, OverlayLevels.Length - 1);
            configuration.OverlayEnabled = configuration.OverlayLevel > 0;

            if (!configuration.OverlayEnabled)
            {
                _settingsStore.Save(configuration);
                UpdateTimerSchedule(configuration);
                _sharedMemory.ReleaseOverlay();
                SetRuntimeStatus(new PerformanceRuntimeStatus
                {
                    HelperProcessId = FindRtssProcessId(),
                    DetailText = "The Tools for Steam RTSS overlay is off.",
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
                return BuildSnapshot(configuration, "RTSS overlay is off.");
            }

            var installation = _installationService.EnsureInstalledAndRunning(allowInstall: true);
            _settingsStore.Save(configuration);
            UpdateTimerSchedule(configuration);
            UpdateOverlayLocked(configuration, installation);
            return BuildSnapshot(
                configuration,
                $"RTSS overlay started in {OverlayLevels[configuration.OverlayLevel].Title} mode.");
        }
    }

    public PerformanceSnapshot ToggleAutoTarget()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            configuration.AutoTargetEnabled = true;
            _settingsStore.Save(configuration);
            UpdateTimerSchedule(configuration);
            return BuildSnapshot(configuration, "RTSS automatically follows the active 3D game.");
        }
    }

    public PerformanceSnapshot SetSettingValue(string key, int value)
    {
        lock (_gate)
        {
            if (key == "frame-limit")
            {
                // RTSS keeps its per-game profiles below Program Files. The installer
                // grants only the signed-in user access to that data folder, but a
                // portable/older install may need the same one-time repair here.
                _installationService.EnsureProfileWriteAccess();
            }

            var configuration = _settingsStore.Load();
            _ = key switch
            {
                "overlay-position" => configuration.OverlayPosition = value,
                "overlay-width" => configuration.OverlayWidth = value,
                "overlay-scale" => configuration.OverlayScale = value,
                "graph-mode" => configuration.GraphMode = value,
                "background-theme" => configuration.BackgroundTheme = value,
                "background-opacity" => configuration.BackgroundOpacity = value,
                "metric-poll-rate" => configuration.MetricPollRate = value,
                "telemetry-period" => configuration.TelemetrySamplingPeriodMs = value,
                "metrics-window" => configuration.MetricsWindow = value,
                "overlay-draw-rate" => configuration.OverlayDrawRate = value,
                "frame-limit" => configuration.FrameLimit = value,
                _ => throw new InvalidOperationException("Unknown RTSS performance setting key.")
            };

            if (key == "frame-limit")
            {
                configuration.FrameLimitConfigured = true;
            }

            _settingsStore.Save(configuration);
            UpdateTimerSchedule(configuration);
            configuration = _settingsStore.Load();
            _lastProfileFingerprint = 0;
            RefreshWhileRunning(configuration);
            return BuildSnapshot(configuration, BuildSettingValueStatusText(key, configuration));
        }
    }

    public PerformanceSnapshot StartOverlay()
    {
        var configuration = _settingsStore.Load();
        return SetOverlayLevel(configuration.OverlayLevel > 0 ? configuration.OverlayLevel : 1);
    }

    public void RestoreOverlayOnStartup()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            if (!configuration.OverlayEnabled && !configuration.FrameLimitConfigured)
            {
                return;
            }

            var installation = _installationService.EnsureInstalledAndRunning(allowInstall: false);
            if (installation.Installed && installation.Running)
            {
                UpdateOverlayLocked(configuration, installation);
            }
        }
    }

    public PerformanceSnapshot PrepareElevatedHelper()
    {
        lock (_gate)
        {
            _sharedMemory.ReleaseOverlay();
            var installation = _installationService.InstallOrRepair();
            var configuration = _settingsStore.Load();
            if (configuration.OverlayEnabled)
            {
                UpdateOverlayLocked(configuration, installation);
            }

            return BuildSnapshot(configuration, $"RTSS {installation.Version} was repaired and restarted.");
        }
    }

    public PerformanceSnapshot StopOverlay()
    {
        return SetOverlayLevel(0);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _updateTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _sharedMemory.ReleaseOverlay();
        }

        _updateTimer.Dispose();
    }

    private void UpdateOverlay()
    {
        if (!Monitor.TryEnter(_gate))
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                return;
            }

            var configuration = _settingsStore.Load();
            if (!configuration.OverlayEnabled && !configuration.FrameLimitConfigured)
            {
                return;
            }

            var installation = _installationService.Detect();
            if (!installation.Running)
            {
                SaveUnavailableStatus("RTSS is not running.");
                return;
            }

            UpdateOverlayLocked(configuration, installation);
        }
        catch (Exception exception)
        {
            SaveUnavailableStatus(exception.Message);
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private void UpdateTimerSchedule(PerformanceSettingsConfiguration configuration)
    {
        if (_disposed)
        {
            return;
        }

        var enabled = configuration.OverlayEnabled || configuration.FrameLimitConfigured;
        _updateTimer.Change(
            enabled ? TimeSpan.FromMilliseconds(250) : Timeout.InfiniteTimeSpan,
            enabled ? TimeSpan.FromMilliseconds(250) : Timeout.InfiniteTimeSpan);
    }

    private void UpdateOverlayLocked(PerformanceSettingsConfiguration configuration, RtssInstallation installation)
    {
        if (!_sharedMemory.TryReadForeground(out var telemetry))
        {
            if (configuration.OverlayEnabled)
            {
                _sharedMemory.TryWriteOverlay(BuildWaitingOverlay(configuration), includeFrametimeGraph: false);
            }
            SetRuntimeStatus(new PerformanceRuntimeStatus
            {
                OverlayVisible = false,
                HelperProcessId = FindRtssProcessId(),
                DetailText = "RTSS is ready and waiting for a 3D game.",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            return;
        }

        var processName = Path.GetFileNameWithoutExtension(telemetry.ExecutableName);
        var window = PerformanceForegroundTargetResolver.TryResolve();
        var title = window?.ProcessId == telemetry.ProcessId ? window.WindowTitle : string.Empty;
        var (cpu, memory) = ReadProcessMetrics(telemetry.ProcessId);
        ApplyProfileIfNeeded(installation, telemetry, configuration);
        var visible = false;
        if (configuration.OverlayEnabled)
        {
            var text = BuildOverlayText(configuration, telemetry, cpu, memory);
            var includeGraph = configuration.OverlayLevel is 3 or 4;
            visible = _sharedMemory.TryWriteOverlay(text, includeGraph);
        }
        SetRuntimeStatus(new PerformanceRuntimeStatus
        {
            OverlayVisible = visible,
            HelperProcessId = FindRtssProcessId(),
            TargetProcessId = telemetry.ProcessId,
            TargetProcessName = processName,
            TargetWindowTitle = title,
            FramesPerSecond = telemetry.FramesPerSecond,
            FrameTimeMs = telemetry.FrameTimeMs,
            OnePercentLowFps = telemetry.OnePercentLowFps,
            FramePacingMs = telemetry.FrameTimeMs,
            TargetCpuPercent = cpu,
            TargetMemoryMb = memory,
            DetailText = visible
                ? $"RTSS is tracking {processName}."
                : configuration.OverlayEnabled
                    ? "RTSS telemetry is active, but the Tools for Steam OSD slot is unavailable."
                    : $"RTSS frame limiting is active for {processName}.",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
    }

    private void ApplyProfileIfNeeded(
        RtssInstallation installation,
        RtssTelemetrySnapshot telemetry,
        PerformanceSettingsConfiguration configuration)
    {
        if (!configuration.FrameLimitConfigured)
        {
            return;
        }

        var fingerprint = configuration.FrameLimit;
        if (_lastProfileProcessId == telemetry.ProcessId && _lastProfileFingerprint == fingerprint)
        {
            return;
        }

        var executableName = Path.GetFileName(telemetry.ExecutableName);
        using var profile = new RtssProfileClient(installation.InstallPath);
        profile.ApplyGameProfile(executableName, configuration.FrameLimit);
        _lastProfileProcessId = telemetry.ProcessId;
        _lastProfileFingerprint = fingerprint;
    }

    private (double Cpu, long MemoryMb) ReadProcessMetrics(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var now = DateTimeOffset.UtcNow;
            var cpuTime = process.TotalProcessorTime;
            var cpu = 0d;
            if (_cpuProcessId == processId && _lastCpuSampleUtc != default)
            {
                var elapsed = (now - _lastCpuSampleUtc).TotalMilliseconds;
                var used = (cpuTime - _lastCpuTime).TotalMilliseconds;
                if (elapsed > 0)
                {
                    cpu = Math.Clamp(used / elapsed * 100d / Environment.ProcessorCount, 0, 100);
                }
            }

            _cpuProcessId = processId;
            _lastCpuTime = cpuTime;
            _lastCpuSampleUtc = now;
            return (cpu, process.WorkingSet64 / 1024 / 1024);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static string BuildOverlayText(
        PerformanceSettingsConfiguration configuration,
        RtssTelemetrySnapshot telemetry,
        double cpu,
        long memory)
    {
        const string cyan = "<C=66C0F4>";
        const string white = "<C=FFFFFF>";
        var low = telemetry.OnePercentLowFps;
        var cap = configuration.FrameLimit <= 0 ? "OFF" : configuration.FrameLimit.ToString();
        var battery = ReadBatteryText();

        var body = configuration.OverlayLevel switch
        {
            1 => $"{cyan}<FR>{white} FPS",
            2 => $"{cyan}FPS {white}<FR>  {cyan}FT {white}<FT>  {cyan}1% {white}{FormatLow(low)}  " +
                 $"{cyan}CPU {white}{cpu:0}%  {cyan}RAM {white}{memory} MB  {cyan}CAP {white}{cap}  {cyan}BAT {white}{battery}",
            3 => $"{cyan}PERFORMANCE\n" +
                 $"{cyan}FPS       {white}<FR>\n" +
                 $"{cyan}FRAME     {white}<FT>\n" +
                 $"{cyan}1% LOW    {white}{FormatLow(low)}\n" +
                 $"{cyan}CPU       {white}{cpu:0.0}%\n" +
                 $"{cyan}RAM       {white}{memory} MB\n" +
                 $"{cyan}API       {white}{telemetry.GraphicsApi}\n" +
                 $"{cyan}CAP       {white}{cap} FPS\n" +
                 $"{cyan}BATTERY   {white}{battery}\n<OBJ=00000000>",
            _ => $"{cyan}FRAME PACING\n" +
                 $"{cyan}FPS {white}<FR>   {cyan}1% LOW {white}{FormatLow(low)}   {cyan}FT {white}<FT>\n" +
                 "<OBJ=00000000>"
        };

        return RtssOverlayFormatter.Wrap(
            body,
            configuration.OverlayPosition,
            configuration.OverlayScale,
            largeFont: configuration.OverlayLevel == 1);
    }

    private static string BuildWaitingOverlay(PerformanceSettingsConfiguration configuration) =>
        configuration.OverlayLevel <= 1
            ? string.Empty
            : RtssOverlayFormatter.Wrap(
                "<C=66C0F4>RTSS<C=FFFFFF>  Waiting for a 3D game",
                configuration.OverlayPosition,
                configuration.OverlayScale,
                largeFont: false);

    private static string FormatLow(double value) => value > 0 ? value.ToString("0.0") : "--";

    private static string ReadBatteryText()
    {
        try
        {
            var power = System.Windows.Forms.SystemInformation.PowerStatus;
            if (power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery))
            {
                return "AC";
            }

            var percentage = Math.Clamp((int)Math.Round(power.BatteryLifePercent * 100), 0, 100);
            return power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online
                ? $"{percentage}%+"
                : $"{percentage}%";
        }
        catch
        {
            return "--";
        }
    }

    private void RefreshWhileRunning(PerformanceSettingsConfiguration configuration)
    {
        if (configuration.OverlayEnabled || configuration.FrameLimitConfigured)
        {
            var installation = _installationService.Detect();
            if (installation.Running)
            {
                UpdateOverlayLocked(configuration, installation);
            }
        }
    }

    private PerformanceSnapshot BuildSnapshot(PerformanceSettingsConfiguration configuration, string? statusOverride = null)
    {
        var installationState = _installationService.Detect();
        var runtimeStatus = _runtimeStatus;
        var level = OverlayLevels[Math.Clamp(configuration.OverlayLevel, 0, OverlayLevels.Length - 1)];
        var rtssPid = FindRtssProcessId();
        var installation = new PerformanceInstallationState(
            installationState.Installed,
            installationState.Running,
            installationState.Installed,
            installationState.Version,
            installationState.InstallPath,
            installationState.ExecutablePath,
            Path.Combine(AppContext.BaseDirectory, "data", "performance.json"));
        var settings = new PerformanceSettingsState(
            configuration.OverlayLevel,
            level.Title,
            level.Description,
            true,
            configuration.OverlayPosition,
            FormatPosition(configuration.OverlayPosition),
            configuration.OverlayWidth,
            configuration.OverlayScale,
            configuration.GraphMode,
            configuration.GraphMode == 0 ? "Off" : "Frametime",
            configuration.BackgroundTheme,
            "RTSS",
            configuration.BackgroundOpacity,
            configuration.MetricPollRate,
            configuration.TelemetrySamplingPeriodMs,
            configuration.MetricsWindow,
            configuration.OverlayDrawRate,
            configuration.FrameLimit,
            configuration.FrameLimit <= 0 ? "Unlimited" : $"{configuration.FrameLimit} FPS",
            OverlayLevels.Select(item => item with { Selected = item.Value == configuration.OverlayLevel }).ToArray());
        var runtime = new PerformanceRuntimeState(
            false,
            configuration.OverlayEnabled && runtimeStatus.OverlayVisible,
            rtssPid,
            runtimeStatus.TargetProcessId,
            runtimeStatus.TargetProcessName,
            runtimeStatus.TargetWindowTitle,
            runtimeStatus.FramesPerSecond,
            runtimeStatus.FrameTimeMs,
            runtimeStatus.OnePercentLowFps,
            runtimeStatus.FramePacingMs,
            runtimeStatus.TargetCpuPercent,
            runtimeStatus.TargetMemoryMb,
            runtimeStatus.DetailText,
            runtimeStatus.ErrorText,
            runtimeStatus.UpdatedAtUtc == DateTimeOffset.MinValue
                ? string.Empty
                : runtimeStatus.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        var vendor = new PerformanceVendorOverlayState(
            "rtss",
            "RivaTuner Statistics Server",
            true,
            installationState.Installed,
            installationState.Running,
            true,
            false,
            string.Empty,
            installationState.Running ? "Running" : installationState.Installed ? "Installed" : "Not installed",
            "Tools for Steam uses an isolated RTSS OSD slot and per-game profiles.");

        return new PerformanceSnapshot(
            installation,
            settings,
            runtime,
            [vendor],
            statusOverride ?? BuildStatusText(configuration, installationState, runtimeStatus));
    }

    private static string BuildStatusText(
        PerformanceSettingsConfiguration configuration,
        RtssInstallation installation,
        PerformanceRuntimeStatus runtime)
    {
        if (!installation.Installed)
        {
            return "RTSS is required. Install it once, then Tools for Steam configures it automatically.";
        }

        if (!configuration.OverlayEnabled)
        {
            if (configuration.FrameLimitConfigured)
            {
                return configuration.FrameLimit <= 0
                    ? $"RTSS {installation.Version} is ready; the per-game limiter is set to Unlimited."
                    : $"RTSS {installation.Version} is ready; the per-game limiter is set to {configuration.FrameLimit} FPS.";
            }

            return $"RTSS {installation.Version} is ready. Move the Overlay Mode slider right to enable it.";
        }

        if (!installation.Running)
        {
            return "RTSS is installed but not running.";
        }

        if (!string.IsNullOrWhiteSpace(runtime.ErrorText))
        {
            return runtime.ErrorText;
        }

        if (runtime.TargetProcessId <= 0)
        {
            return "RTSS is waiting for a 3D game.";
        }

        return $"RTSS is tracking {runtime.TargetProcessName} at {runtime.FramesPerSecond:0.#} FPS.";
    }

    private static string BuildSettingValueStatusText(string key, PerformanceSettingsConfiguration configuration) => key switch
    {
        "frame-limit" => configuration.FrameLimit <= 0
            ? "Per-game RTSS frame limit disabled."
            : $"Per-game RTSS frame limit set to {configuration.FrameLimit} FPS.",
        "overlay-position" => $"RTSS overlay position set to {FormatPosition(configuration.OverlayPosition)}.",
        "overlay-scale" => $"RTSS overlay scale set to {configuration.OverlayScale}%.",
        _ => "RTSS overlay setting updated."
    };

    private static string FormatPosition(int value) => value switch
    {
        1 => "Top Right",
        2 => "Bottom Left",
        3 => "Bottom Right",
        _ => "Top Left"
    };

    private static int FindRtssProcessId()
    {
        var processes = Process.GetProcessesByName("RTSS");
        try
        {
            return processes.FirstOrDefault(process => !process.HasExited)?.Id ?? 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private void SaveUnavailableStatus(string errorText)
    {
        SetRuntimeStatus(new PerformanceRuntimeStatus
        {
            HelperProcessId = FindRtssProcessId(),
            DetailText = errorText,
            ErrorText = errorText,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
    }

    private void SetRuntimeStatus(PerformanceRuntimeStatus status)
    {
        _runtimeStatus = status;
    }
}

internal static class RtssOverlayFormatter
{
    public static string Wrap(string text, int position, int scale, bool largeFont)
    {
        var (positionTag, layerTag) = Math.Clamp(position, 0, 3) switch
        {
            1 => ("<P2>", "<L1>"),
            2 => ("<P6>", "<L2>"),
            3 => ("<P8>", "<L3>"),
            _ => ("<P0>", "<L0>")
        };
        var zoomRatio = Math.Clamp((int)Math.Round(scale / 50d, MidpointRounding.AwayFromZero), 1, 4);
        var fontHeight = largeFont ? -18 : -9;
        var fontWeight = largeFont ? 700 : 400;

        // These are RTSS 2.11+ hypertext tags. Keeping presentation in our own
        // OSD slot avoids changing the user's global RTSS layout/profile.
        return $"{positionTag}{layerTag}<FNT=Unispace,{fontHeight},{fontWeight},{zoomRatio}>{text}";
    }
}

internal sealed class PerformanceRuntimeStatus
{
    public bool OverlayVisible { get; set; }

    public int HelperProcessId { get; set; }

    public int TargetProcessId { get; set; }

    public string TargetProcessName { get; set; } = string.Empty;

    public string TargetWindowTitle { get; set; } = string.Empty;

    public double FramesPerSecond { get; set; }

    public double FrameTimeMs { get; set; }

    public double OnePercentLowFps { get; set; }

    public double FramePacingMs { get; set; }

    public double TargetCpuPercent { get; set; }

    public long TargetMemoryMb { get; set; }

    public string DetailText { get; set; } = string.Empty;

    public string ErrorText { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.MinValue;
}
