namespace SteamLoader.App.Models;

public sealed record PerformanceOverlayLevelState(
    int Value,
    string Title,
    string Description,
    bool Selected);

public sealed record PerformanceInstallationState(
    bool Installed,
    bool Running,
    bool ElevatedHelperReady,
    string Version,
    string InstallPath,
    string ExecutablePath,
    string PreferencesPath);

public sealed record PerformanceRuntimeState(
    bool Elevated,
    bool OverlayVisible,
    int HelperProcessId,
    int TargetProcessId,
    string TargetProcessName,
    string TargetWindowTitle,
    double FramesPerSecond,
    double FrameTimeMs,
    double OnePercentLowFps,
    double FramePacingMs,
    double TargetCpuPercent,
    long TargetMemoryMb,
    string DetailText,
    string ErrorText,
    string UpdatedAt);

public sealed record PerformanceVendorOverlayState(
    string Id,
    string Title,
    bool Supported,
    bool Installed,
    bool Active,
    bool StateDetected,
    bool HotkeyDetected,
    string Hotkey,
    string StatusText,
    string DetailText);

public sealed record PerformanceSettingsState(
    int OverlayLevel,
    string OverlayLevelTitle,
    string OverlayLevelDescription,
    bool AutoTargetEnabled,
    int OverlayPosition,
    string OverlayPositionTitle,
    int OverlayWidth,
    int OverlayScale,
    int GraphMode,
    string GraphModeTitle,
    int BackgroundTheme,
    string BackgroundThemeTitle,
    int BackgroundOpacity,
    int MetricPollRate,
    int TelemetrySamplingPeriodMs,
    int MetricsWindow,
    int OverlayDrawRate,
    IReadOnlyList<PerformanceOverlayLevelState> OverlayLevels);

public sealed record PerformanceSnapshot(
    PerformanceInstallationState Installation,
    PerformanceSettingsState Settings,
    PerformanceRuntimeState Runtime,
    IReadOnlyList<PerformanceVendorOverlayState> VendorOverlays,
    string StatusText);
