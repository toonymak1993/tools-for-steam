using System.Diagnostics;
using System.Threading;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.Performance;

public sealed class TfsPerformanceService
{
    private static readonly PerformanceOverlayLevelState[] OverlayLevels =
    [
        new(0, "SteamOS Strip", "Slim MangoHud-style strip for FPS, frametime, CPU, RAM, and quick status.", false),
        new(1, "SteamOS Full", "Detailed left-side MangoHud-style panel with a stat column and live graph.", false),
        new(2, "SteamOS Compact", "Compact SteamOS-inspired block with large values and a bottom graph.", false)
    ];

    private static readonly string HelperExecutablePath =
        Environment.ProcessPath
        ?? throw new InvalidOperationException("Unable to resolve the Tools for Steam executable path.");

    private readonly PerformanceSettingsStore _settingsStore;
    private readonly PerformanceStatusStore _statusStore;
    private readonly FpsHelperScheduledTaskService _taskService;
    private readonly object _gate = new();

    public TfsPerformanceService(PerformanceSettingsStore settingsStore, PerformanceStatusStore statusStore)
    {
        _settingsStore = settingsStore;
        _statusStore = statusStore;
        _taskService = new FpsHelperScheduledTaskService(
            HelperExecutablePath,
            SteamLoaderRuntime.FpsHelperArgument,
            AppContext.BaseDirectory);
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
            _settingsStore.Save(configuration);
            return BuildSnapshot(configuration, $"Overlay level set to {OverlayLevels[configuration.OverlayLevel].Title}.");
        }
    }

    public PerformanceSnapshot ToggleAutoTarget()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            configuration.AutoTargetEnabled = !configuration.AutoTargetEnabled;
            _settingsStore.Save(configuration);

            return BuildSnapshot(
                configuration,
                configuration.AutoTargetEnabled
                    ? "Auto target enabled."
                    : "Auto target disabled.");
        }
    }

    public PerformanceSnapshot SetSettingValue(string key, int value)
    {
        lock (_gate)
        {
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
                _ => throw new InvalidOperationException("Unknown TFS FPS Overlay setting key.")
            };

            _settingsStore.Save(configuration);
            configuration = _settingsStore.Load();
            return BuildSnapshot(configuration, BuildSettingValueStatusText(key, configuration));
        }
    }

    public PerformanceSnapshot StartOverlay()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            var previouslyEnabled = configuration.OverlayEnabled;
            configuration.OverlayEnabled = true;
            _settingsStore.Save(configuration);

            try
            {
                EnsureHelperRunning(allowRegistrationPrompt: true);
                return BuildSnapshot(configuration, $"Starting TFS FPS Overlay with {OverlayLevels[configuration.OverlayLevel].Title}.");
            }
            catch
            {
                if (!previouslyEnabled)
                {
                    configuration.OverlayEnabled = false;
                    _settingsStore.Save(configuration);
                }

                throw;
            }
        }
    }

    public void RestoreOverlayOnStartup()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            if (!configuration.OverlayEnabled)
            {
                return;
            }

            _ = TryEnsureHelperRunning(allowRegistrationPrompt: false, out _, out _);
        }
    }

    public PerformanceSnapshot PrepareElevatedHelper()
    {
        lock (_gate)
        {
            if (_taskService.IsRegistered())
            {
                return BuildSnapshot(_settingsStore.Load(), "The elevated TFS FPS helper is already prepared.");
            }

            if (!_taskService.TryEnsureRegistered(out var errorText, out var cancelledByUser))
            {
                throw new InvalidOperationException(cancelledByUser
                    ? errorText
                    : $"The elevated TFS FPS helper could not be prepared. {errorText}".Trim());
            }

            return BuildSnapshot(_settingsStore.Load(), "The elevated TFS FPS helper is ready.");
        }
    }

    public PerformanceSnapshot StopOverlay()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            configuration.OverlayEnabled = false;
            _settingsStore.Save(configuration);
            return BuildSnapshot(configuration, "TFS FPS Overlay stopped.");
        }
    }

    private void EnsureHelperRunning(bool allowRegistrationPrompt)
    {
        if (TryEnsureHelperRunning(allowRegistrationPrompt, out var errorText, out _))
        {
            return;
        }

        throw new InvalidOperationException(errorText);
    }

    private bool TryEnsureHelperRunning(bool allowRegistrationPrompt, out string errorText, out bool registrationMissing)
    {
        errorText = string.Empty;
        registrationMissing = false;

        var status = _statusStore.Load();
        if (IsHelperRunning(status) && status.Elevated)
        {
            return true;
        }

        if (IsHelperRunning(status) && !status.Elevated)
        {
            TryStopHelperProcess(status.HelperProcessId);
            status = new PerformanceRuntimeStatus();
            _statusStore.Save(status);
        }

        if (allowRegistrationPrompt)
        {
            if (!_taskService.TryEnsureRegistered(out var registrationError, out var cancelledByUser))
            {
                errorText = cancelledByUser
                    ? registrationError
                    : $"The elevated TFS FPS helper could not be prepared. {registrationError}".Trim();
                SaveHelperStartupFailure(errorText);
                return false;
            }
        }
        else if (!_taskService.IsRegistered())
        {
            registrationMissing = true;
            return false;
        }

        _statusStore.Save(new PerformanceRuntimeStatus
        {
            DetailText = "Starting the elevated TFS FPS helper...",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });

        if (!_taskService.TryRun(out var startError))
        {
            errorText = $"The elevated TFS FPS helper could not be started. {startError}".Trim();
            SaveHelperStartupFailure(errorText);
            return false;
        }

        if (!WaitForElevatedHelperStartup(TimeSpan.FromSeconds(6), out var startedStatus))
        {
            errorText = string.IsNullOrWhiteSpace(startedStatus.ErrorText)
                ? "The elevated TFS FPS helper did not report ready in time."
                : startedStatus.ErrorText;
            SaveHelperStartupFailure(errorText);
            return false;
        }

        return true;
    }

    private PerformanceSnapshot BuildSnapshot(PerformanceSettingsConfiguration configuration, string? statusOverride = null)
    {
        var runtimeStatus = _statusStore.Load();
        var helperRunning = IsHelperRunning(runtimeStatus);
        var elevatedHelperReady = _taskService.IsRegistered();
        if (!helperRunning)
        {
            runtimeStatus = new PerformanceRuntimeStatus
            {
                Elevated = runtimeStatus.Elevated,
                DetailText = configuration.OverlayEnabled
                    ? elevatedHelperReady
                        ? "TFS FPS Overlay is waiting for the elevated helper."
                        : "Prepare the elevated TFS FPS helper once to enable silent ETW capture."
                    : "TFS FPS Overlay is idle.",
                UpdatedAtUtc = runtimeStatus.UpdatedAtUtc
            };
        }
        var levelDefinition = OverlayLevels[Math.Clamp(configuration.OverlayLevel, 0, OverlayLevels.Length - 1)];
        var installation = new PerformanceInstallationState(
            Installed: true,
            Running: helperRunning && configuration.OverlayEnabled,
            ElevatedHelperReady: elevatedHelperReady,
            Version: GetProductVersion(),
            InstallPath: AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            ExecutablePath: HelperExecutablePath,
            PreferencesPath: Path.Combine(AppContext.BaseDirectory, "data", "performance.json"));
        var settings = new PerformanceSettingsState(
            OverlayLevel: configuration.OverlayLevel,
            OverlayLevelTitle: levelDefinition.Title,
            OverlayLevelDescription: levelDefinition.Description,
            AutoTargetEnabled: configuration.AutoTargetEnabled,
            OverlayPosition: configuration.OverlayPosition,
            OverlayPositionTitle: configuration.OverlayPosition switch
            {
                1 => "Top Right",
                2 => "Bottom Left",
                3 => "Bottom Right",
                _ => "Top Left"
            },
            OverlayWidth: configuration.OverlayWidth,
            OverlayScale: configuration.OverlayScale,
            GraphMode: configuration.GraphMode,
            GraphModeTitle: configuration.GraphMode switch
            {
                1 => "FPS",
                2 => "Frametime",
                _ => "Off"
            },
            BackgroundTheme: configuration.BackgroundTheme,
            BackgroundThemeTitle: configuration.BackgroundTheme switch
            {
                1 => "Slate",
                2 => "Midnight",
                3 => "Graphite",
                4 => "Frost",
                _ => "Steam Blue"
            },
            BackgroundOpacity: configuration.BackgroundOpacity,
            MetricPollRate: configuration.MetricPollRate,
            TelemetrySamplingPeriodMs: configuration.TelemetrySamplingPeriodMs,
            MetricsWindow: configuration.MetricsWindow,
            OverlayDrawRate: configuration.OverlayDrawRate,
            OverlayLevels: OverlayLevels
                .Select(level => level with { Selected = level.Value == configuration.OverlayLevel })
                .ToArray());
        var runtime = new PerformanceRuntimeState(
            Elevated: runtimeStatus.Elevated,
            OverlayVisible: runtimeStatus.OverlayVisible,
            HelperProcessId: runtimeStatus.HelperProcessId,
            TargetProcessId: runtimeStatus.TargetProcessId,
            TargetProcessName: runtimeStatus.TargetProcessName,
            TargetWindowTitle: runtimeStatus.TargetWindowTitle,
            FramesPerSecond: runtimeStatus.FramesPerSecond,
            FrameTimeMs: runtimeStatus.FrameTimeMs,
            OnePercentLowFps: runtimeStatus.OnePercentLowFps,
            FramePacingMs: runtimeStatus.FramePacingMs,
            TargetCpuPercent: runtimeStatus.TargetCpuPercent,
            TargetMemoryMb: runtimeStatus.TargetMemoryMb,
            DetailText: runtimeStatus.DetailText,
            ErrorText: runtimeStatus.ErrorText,
            UpdatedAt: runtimeStatus.UpdatedAtUtc == DateTimeOffset.MinValue
                ? string.Empty
                : runtimeStatus.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));

        return new PerformanceSnapshot(
            installation,
            settings,
            runtime,
            [],
            statusOverride ?? BuildStatusText(configuration, runtimeStatus, helperRunning, elevatedHelperReady));
    }

    private static bool IsHelperRunning(PerformanceRuntimeStatus status)
    {
        if (status.HelperProcessId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(status.HelperProcessId);
            return !process.HasExited && string.Equals(process.ProcessName, "ToolsForSteam", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildStatusText(
        PerformanceSettingsConfiguration configuration,
        PerformanceRuntimeStatus status,
        bool helperRunning,
        bool elevatedHelperReady)
    {
        if (!configuration.OverlayEnabled)
        {
            return elevatedHelperReady
                ? "TFS FPS Overlay is ready. Press Start TFS FPS Overlay when you want it on screen."
                : "Prepare the elevated TFS FPS helper once, then the overlay can start silently and restore after restarts.";
        }

        if (!elevatedHelperReady)
        {
            return "The elevated TFS FPS helper still needs one-time Windows admin setup.";
        }

        if (!helperRunning)
        {
            return "TFS FPS Overlay is waiting for the elevated helper.";
        }

        if (!string.IsNullOrWhiteSpace(status.ErrorText))
        {
            return status.ErrorText;
        }

        if (status.TargetProcessId <= 0)
        {
            return configuration.AutoTargetEnabled
                ? "TFS FPS Overlay is waiting for an active game window."
                : "TFS FPS Overlay is waiting for a manual target window.";
        }

        return status.FramesPerSecond > 0
            ? $"Tracking {status.TargetProcessName} at {status.FramesPerSecond:0.#} FPS."
            : $"Attached to {status.TargetProcessName} and waiting for frame events.";
    }

    private static string BuildSettingValueStatusText(string key, PerformanceSettingsConfiguration configuration)
    {
        return key switch
        {
            "overlay-position" => $"Overlay position set to {configuration.OverlayPosition switch
            {
                1 => "Top Right",
                2 => "Bottom Left",
                3 => "Bottom Right",
                _ => "Top Left"
            }}.",
            "overlay-width" => $"Overlay width set to {configuration.OverlayWidth}px.",
            "overlay-scale" => $"Overlay scale set to {configuration.OverlayScale}%.",
            "graph-mode" => $"Graph mode set to {configuration.GraphMode switch
            {
                1 => "FPS",
                2 => "Frametime",
                _ => "Off"
            }}.",
            "background-theme" => "Overlay theme updated.",
            "background-opacity" => $"Transparency set to {configuration.BackgroundOpacity}%.",
            "metric-poll-rate" => $"Polling rate set to {configuration.MetricPollRate} Hz.",
            "telemetry-period" => $"Telemetry period set to {configuration.TelemetrySamplingPeriodMs} ms.",
            "metrics-window" => $"Metrics window set to {configuration.MetricsWindow} ms.",
            "overlay-draw-rate" => $"Draw rate set to {configuration.OverlayDrawRate} Hz.",
            _ => "Performance setting updated."
        };
    }

    private static string GetProductVersion()
    {
        var version = FileVersionInfo.GetVersionInfo(HelperExecutablePath).ProductVersion;
        return string.IsNullOrWhiteSpace(version) ? "Built-in" : version;
    }

    private bool WaitForElevatedHelperStartup(TimeSpan timeout, out PerformanceRuntimeStatus status)
    {
        var stopwatch = Stopwatch.StartNew();
        status = new PerformanceRuntimeStatus();

        while (stopwatch.Elapsed < timeout)
        {
            status = _statusStore.Load();
            if (IsHelperRunning(status) && status.Elevated)
            {
                return true;
            }

            Thread.Sleep(150);
        }

        status = _statusStore.Load();
        return false;
    }

    private static void TryStopHelperProcess(int helperProcessId)
    {
        if (helperProcessId <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(helperProcessId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch
        {
        }
    }

    private void SaveHelperStartupFailure(string detail)
    {
        _statusStore.Save(new PerformanceRuntimeStatus
        {
            DetailText = detail,
            ErrorText = detail,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
    }
}
