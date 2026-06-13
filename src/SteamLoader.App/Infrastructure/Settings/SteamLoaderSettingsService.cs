using SteamLoader.App;
using SteamLoader.App.Models;
using SteamLoader.App.Services;
using System.Reflection;
using System.Text.Json;

namespace SteamLoader.App.Infrastructure.Settings;

public sealed class SteamLoaderSettingsService
{
    private static readonly SteamLoaderPluginDefinition[] PluginDefinitions =
    [
        new("processes", "Processes", "Window switcher for visible app windows.", true),
        new("app-start", "App Start", "Controller launcher for selected Windows apps.", true),
        new("store-sync", "Store Sync", "Launcher sync, Steam shortcuts, and artwork updates.", true),
        new("auto-sisr", "Auto SISR", "Starts SISR marker mode for selected non-Steam games.", true, false),
        new("artwork", "SteamGridDB", "Context menu artwork picker and manual artwork settings.", true),
        new("audio", "Audio", "Output device switching and system volume controls.", true),
        new("display", "Display", "Display switching, resolution, and refresh rate controls.", true),
        new("hltb", "HLTB", "HowLongToBeat game page estimates.", true),
        new("themes", "Themes", "Theme engine, theme store, and profiles.", true),
        new("power", "Power", "Recovery and power actions. This stays available for safety.", false)
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private const int MaximumSplashCloseDelaySeconds = 30;

    private readonly WindowsAutostartService _autostartService;
    private readonly WindowsShellService _shellService;
    private readonly string _executablePath;
    private readonly string _shellLaunchArguments;
    private readonly string _settingsPath;
    private readonly object _gate = new();

    public SteamLoaderSettingsService(
        WindowsAutostartService autostartService,
        WindowsShellService shellService,
        string executablePath,
        string shellLaunchArguments,
        string settingsPath)
    {
        _autostartService = autostartService;
        _shellService = shellService;
        _executablePath = executablePath;
        _shellLaunchArguments = shellLaunchArguments;
        _settingsPath = settingsPath;
    }

    public SteamLoaderGeneralSettingsSnapshot GetSnapshot()
    {
        var settings = LoadSettings();
        var startupMode = ResolveStartupMode(settings);
        return new SteamLoaderGeneralSettingsSnapshot(
            RunOnWindowsSignIn: !string.Equals(startupMode, SteamLoaderRuntime.StartupModeManual, StringComparison.OrdinalIgnoreCase),
            StartupMode: startupMode,
            HideWindowsShellInConsoleMode: settings.HideWindowsShellInConsoleMode ?? true,
            FirstRunCompleted: settings.FirstRunCompleted == true,
            ConsoleModeDefaultApplied: settings.ConsoleModeDefaultApplied == true,
            SplashScreen: BuildSplashScreenSettings(settings),
            ProductVersion: GetProductVersion(),
            InstallPath: AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            Plugins: BuildPluginStates(settings));
    }

    public SteamLoaderGeneralSettingsSnapshot EnsureDefaultConsoleModeEnabled()
    {
        var settings = LoadSettings();
        if (settings.ConsoleModeDefaultApplied == true || settings.RunOnWindowsSignInUserConfigured == true)
        {
            return GetSnapshot();
        }

        ApplyStartupMode(SteamLoaderRuntime.StartupModeShell);
        SaveSettings(settings with
        {
            StartupMode = SteamLoaderRuntime.StartupModeShell,
            ConsoleModeDefaultApplied = true,
            HideWindowsShellInConsoleMode = settings.HideWindowsShellInConsoleMode ?? true
        });

        return GetSnapshot();
    }

    public SteamLoaderGeneralSettingsSnapshot SetRunOnWindowsSignIn(bool enabled)
    {
        var mode = enabled ? SteamLoaderRuntime.StartupModeShell : SteamLoaderRuntime.StartupModeManual;
        ApplyStartupMode(mode);

        var settings = LoadSettings();
        SaveSettings(settings with
        {
            StartupMode = mode,
            ConsoleModeDefaultApplied = true,
            RunOnWindowsSignInUserConfigured = true
        });

        return GetSnapshot();
    }

    public SteamLoaderGeneralSettingsSnapshot SetStartupMode(string mode)
    {
        var normalizedMode = NormalizeStartupMode(mode);
        ApplyStartupMode(normalizedMode);

        var settings = LoadSettings();
        SaveSettings(settings with
        {
            StartupMode = normalizedMode,
            ConsoleModeDefaultApplied = true,
            RunOnWindowsSignInUserConfigured = true
        });

        return GetSnapshot();
    }

    private void ApplyStartupMode(string mode)
    {
        if (!string.Equals(mode, SteamLoaderRuntime.StartupModeManual, StringComparison.OrdinalIgnoreCase))
        {
            _autostartService.DisableSteamAutostartEntries();
        }

        switch (NormalizeStartupMode(mode))
        {
            case SteamLoaderRuntime.StartupModeShell:
                _autostartService.SetEnabled(_executablePath, SteamLoaderRuntime.AutostartArguments, false);
                _shellService.SetEnabled(_executablePath, _shellLaunchArguments, true);
                break;
            case SteamLoaderRuntime.StartupModeTray:
                _shellService.SetEnabled(_executablePath, _shellLaunchArguments, false);
                _autostartService.SetEnabled(_executablePath, SteamLoaderRuntime.AutostartArguments, true);
                break;
            default:
                _autostartService.SetEnabled(_executablePath, SteamLoaderRuntime.AutostartArguments, false);
                _shellService.SetEnabled(_executablePath, _shellLaunchArguments, false);
                break;
        }
    }

    public SteamLoaderGeneralSettingsSnapshot SetHideWindowsShellInConsoleMode(bool enabled)
    {
        var settings = LoadSettings() with
        {
            HideWindowsShellInConsoleMode = enabled
        };

        SaveSettings(settings);
        return GetSnapshot();
    }

    public SteamLoaderGeneralSettingsSnapshot SetSplashScreenEnabled(bool enabled)
    {
        var settings = LoadSettings();
        var splashScreen = NormalizeSplashScreenSettings(settings.SplashScreen) with
        {
            Enabled = enabled
        };

        SaveSettings(settings with { SplashScreen = splashScreen });
        return GetSnapshot();
    }

    public SteamLoaderGeneralSettingsSnapshot SetSplashScreenShowText(bool enabled)
    {
        var settings = LoadSettings();
        var splashScreen = NormalizeSplashScreenSettings(settings.SplashScreen) with
        {
            ShowText = enabled
        };

        SaveSettings(settings with { SplashScreen = splashScreen });
        return GetSnapshot();
    }

    public SteamLoaderGeneralSettingsSnapshot SetSplashScreenWallpaperPath(string? path)
    {
        var settings = LoadSettings();
        var splashScreen = NormalizeSplashScreenSettings(settings.SplashScreen) with
        {
            WallpaperPath = NormalizeOptionalPath(path)
        };

        SaveSettings(settings with { SplashScreen = splashScreen });
        return GetSnapshot();
    }

    public SteamLoaderGeneralSettingsSnapshot SetSplashScreenIconPath(string? path)
    {
        var settings = LoadSettings();
        var splashScreen = NormalizeSplashScreenSettings(settings.SplashScreen) with
        {
            IconPath = NormalizeOptionalPath(path)
        };

        SaveSettings(settings with { SplashScreen = splashScreen });
        return GetSnapshot();
    }

    public SteamLoaderGeneralSettingsSnapshot SetSplashScreenExtraCloseDelaySeconds(int seconds)
    {
        var settings = LoadSettings();
        var splashScreen = NormalizeSplashScreenSettings(settings.SplashScreen) with
        {
            ExtraCloseDelaySeconds = ClampSplashCloseDelay(seconds)
        };

        SaveSettings(settings with { SplashScreen = splashScreen });
        return GetSnapshot();
    }

    public SteamLoaderSplashScreenSettingsSnapshot GetSplashScreenSettings()
    {
        return BuildSplashScreenSettings(LoadSettings());
    }

    public SteamLoaderGeneralSettingsSnapshot SetPluginEnabled(string pluginId, bool enabled)
    {
        var definition = PluginDefinitions.FirstOrDefault(plugin =>
            string.Equals(plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            throw new InvalidOperationException("Unknown plugin.");
        }

        if (!definition.CanDisable && !enabled)
        {
            throw new InvalidOperationException("This plugin cannot be disabled.");
        }

        var settings = LoadSettings();
        var pluginStates = NormalizePluginStates(settings.PluginEnabled);
        pluginStates[definition.Id] = enabled || !definition.CanDisable;

        SaveSettings(settings with
        {
            PluginEnabled = pluginStates
        });

        return GetSnapshot();
    }

    public bool IsPluginEnabled(string pluginId)
    {
        if (string.Equals(pluginId, "settings", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var definition = PluginDefinitions.FirstOrDefault(plugin =>
            string.Equals(plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return true;
        }

        if (!definition.CanDisable)
        {
            return true;
        }

        var settings = LoadSettings();
        var pluginStates = NormalizePluginStates(settings.PluginEnabled);
        return pluginStates.TryGetValue(definition.Id, out var enabled) ? enabled : true;
    }

    public SteamLoaderGeneralSettingsSnapshot CompleteFirstRunSetup()
    {
        var settings = LoadSettings() with
        {
            FirstRunCompleted = true,
            FirstRunCompletedAtUtc = DateTimeOffset.UtcNow
        };

        SaveSettings(settings);
        return GetSnapshot();
    }

    public bool ShouldHideWindowsShellInConsoleMode()
    {
        return LoadSettings().HideWindowsShellInConsoleMode ?? true;
    }

    public bool ShouldShowSplashScreen()
    {
        return NormalizeSplashScreenSettings(LoadSettings().SplashScreen).Enabled == true;
    }

    private SteamLoaderSettingsData LoadSettings()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return new SteamLoaderSettingsData();
                }

                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<SteamLoaderSettingsData>(json, JsonOptions)
                    ?? new SteamLoaderSettingsData();
            }
            catch
            {
                return new SteamLoaderSettingsData();
            }
        }
    }

    private void SaveSettings(SteamLoaderSettingsData settings)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
    }

    private static string GetProductVersion()
    {
        return typeof(SteamLoaderSettingsService)
            .Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "0.0.0-dev";
    }

    private static IReadOnlyList<SteamLoaderPluginSettingsState> BuildPluginStates(SteamLoaderSettingsData settings)
    {
        var pluginStates = NormalizePluginStates(settings.PluginEnabled);

        return PluginDefinitions
            .Select(plugin => new SteamLoaderPluginSettingsState(
                plugin.Id,
                plugin.Title,
                plugin.Description,
                plugin.CanDisable ? pluginStates[plugin.Id] : true,
                plugin.CanDisable))
            .ToArray();
    }

    private static SteamLoaderSplashScreenSettingsSnapshot BuildSplashScreenSettings(SteamLoaderSettingsData settings)
    {
        var splashScreen = NormalizeSplashScreenSettings(settings.SplashScreen);
        var wallpaperPath = splashScreen.WallpaperPath ?? string.Empty;
        var iconPath = splashScreen.IconPath ?? string.Empty;

        return new SteamLoaderSplashScreenSettingsSnapshot(
            splashScreen.Enabled ?? true,
            splashScreen.ShowText ?? true,
            wallpaperPath,
            !string.IsNullOrWhiteSpace(wallpaperPath) && File.Exists(wallpaperPath),
            iconPath,
            !string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath),
            ClampSplashCloseDelay(splashScreen.ExtraCloseDelaySeconds ?? 0));
    }

    private static SteamLoaderSplashScreenSettingsData NormalizeSplashScreenSettings(
        SteamLoaderSplashScreenSettingsData? settings)
    {
        return new SteamLoaderSplashScreenSettingsData
        {
            Enabled = settings?.Enabled ?? true,
            ShowText = settings?.ShowText ?? true,
            WallpaperPath = NormalizeOptionalPath(settings?.WallpaperPath ?? string.Empty),
            IconPath = NormalizeOptionalPath(settings?.IconPath ?? string.Empty),
            ExtraCloseDelaySeconds = ClampSplashCloseDelay(settings?.ExtraCloseDelaySeconds ?? 0)
        };
    }

    private static string NormalizeOptionalPath(string? path)
    {
        return (path ?? string.Empty).Trim().Trim('"');
    }

    private string ResolveStartupMode(SteamLoaderSettingsData settings)
    {
        if (_shellService.IsEnabled(_executablePath, _shellLaunchArguments))
        {
            return SteamLoaderRuntime.StartupModeShell;
        }

        if (_autostartService.IsEnabled(_executablePath, SteamLoaderRuntime.AutostartArguments))
        {
            return SteamLoaderRuntime.StartupModeTray;
        }

        return NormalizeStartupMode(settings.StartupMode);
    }

    private static string NormalizeStartupMode(string? mode)
    {
        return mode?.Trim().ToLowerInvariant() switch
        {
            SteamLoaderRuntime.StartupModeShell => SteamLoaderRuntime.StartupModeShell,
            SteamLoaderRuntime.StartupModeTray => SteamLoaderRuntime.StartupModeTray,
            SteamLoaderRuntime.StartupModeManual => SteamLoaderRuntime.StartupModeManual,
            _ => SteamLoaderRuntime.StartupModeManual
        };
    }

    private static int ClampSplashCloseDelay(int seconds)
    {
        return Math.Clamp(seconds, 0, MaximumSplashCloseDelaySeconds);
    }

    private static Dictionary<string, bool> NormalizePluginStates(Dictionary<string, bool>? savedStates)
    {
        var normalized = PluginDefinitions.ToDictionary(
            plugin => plugin.Id,
            plugin => plugin.DefaultEnabled,
            StringComparer.OrdinalIgnoreCase);

        if (savedStates is null)
        {
            return normalized;
        }

        foreach (var plugin in PluginDefinitions)
        {
            if (!plugin.CanDisable)
            {
                normalized[plugin.Id] = true;
                continue;
            }

            if (savedStates.TryGetValue(plugin.Id, out var enabled))
            {
                normalized[plugin.Id] = enabled;
            }
        }

        return normalized;
    }

    private sealed record SteamLoaderPluginDefinition(
        string Id,
        string Title,
        string Description,
        bool CanDisable,
        bool DefaultEnabled = true);

    private sealed record SteamLoaderSettingsData
    {
        public bool? ConsoleModeDefaultApplied { get; init; }

        public bool? RunOnWindowsSignInUserConfigured { get; init; }

        public string? StartupMode { get; init; }

        public bool? HideWindowsShellInConsoleMode { get; init; }

        public bool? FirstRunCompleted { get; init; }

        public DateTimeOffset? FirstRunCompletedAtUtc { get; init; }

        public Dictionary<string, bool>? PluginEnabled { get; init; }

        public SteamLoaderSplashScreenSettingsData? SplashScreen { get; init; }
    }

    private sealed record SteamLoaderSplashScreenSettingsData
    {
        public bool? Enabled { get; init; }

        public bool? ShowText { get; init; }

        public string? WallpaperPath { get; init; }

        public string? IconPath { get; init; }

        public int? ExtraCloseDelaySeconds { get; init; }
    }
}
