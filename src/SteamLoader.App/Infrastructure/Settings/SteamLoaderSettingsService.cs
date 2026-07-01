using SteamLoader.App;
using SteamLoader.App.Models;
using SteamLoader.App.Services;
using System.Reflection;
using System.Text.Json;

namespace SteamLoader.App.Infrastructure.Settings;

public sealed class SteamLoaderSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private const int MaximumWindowsShellStartDelaySeconds = 30;

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
            RunOnWindowsSignIn: true,
            StartupMode: startupMode,
            HideWindowsShellInConsoleMode: settings.HideWindowsShellInConsoleMode ?? true,
            FirstRunCompleted: settings.FirstRunCompleted == true,
            ConsoleModeDefaultApplied: settings.ConsoleModeDefaultApplied == true,
            SplashScreen: BuildSplashScreenSettings(settings),
            WindowsShellStartDelaySeconds: GetWindowsShellStartDelaySeconds(settings),
            ProductVersion: GetProductVersion(),
            InstallPath: AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            DeveloperDebugEnabled: settings.DeveloperDebugEnabled == true,
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
        var mode = enabled ? SteamLoaderRuntime.StartupModeShell : SteamLoaderRuntime.StartupModeTray;
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
        _autostartService.DisableSteamAutostartEntries();

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
                _shellService.SetEnabled(_executablePath, _shellLaunchArguments, true);
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

    public SteamLoaderGeneralSettingsSnapshot SetDeveloperDebugEnabled(bool enabled)
    {
        var settings = LoadSettings() with
        {
            DeveloperDebugEnabled = enabled
        };

        SaveSettings(settings);
        return GetSnapshot();
    }

    public string GetUpdateChannel()
    {
        return NormalizeUpdateChannel(LoadSettings().UpdateChannel);
    }

    public string SetUpdateChannel(string channel)
    {
        var normalizedChannel = NormalizeUpdateChannel(channel);
        var settings = LoadSettings() with
        {
            UpdateChannel = normalizedChannel
        };

        SaveSettings(settings);
        return normalizedChannel;
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

    public SteamLoaderGeneralSettingsSnapshot SetWindowsShellStartDelaySeconds(int seconds)
    {
        var settings = LoadSettings() with
        {
            WindowsShellStartDelaySeconds = ClampWindowsShellStartDelay(seconds)
        };

        SaveSettings(settings);
        return GetSnapshot();
    }

    public SteamLoaderSplashScreenSettingsSnapshot GetSplashScreenSettings()
    {
        return BuildSplashScreenSettings(LoadSettings());
    }

    public SteamLoaderGeneralSettingsSnapshot SetPluginEnabled(string pluginId, bool enabled)
    {
        var definition = SteamLoaderPluginCatalog.Find(pluginId);
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

    public SteamLoaderGeneralSettingsSnapshot SetPluginOrder(IReadOnlyList<string>? pluginIds)
    {
        var normalizedOrder = NormalizePluginOrder(pluginIds);
        var settings = LoadSettings() with
        {
            PluginOrder = normalizedOrder.ToArray()
        };

        SaveSettings(settings);
        return GetSnapshot();
    }

    public bool IsPluginEnabled(string pluginId)
    {
        if (string.Equals(pluginId, "settings", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var definition = SteamLoaderPluginCatalog.Find(pluginId);
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
        var orderedPluginIds = NormalizePluginOrder(settings.PluginOrder);
        var orderedDefinitions = orderedPluginIds
            .Select(id => SteamLoaderPluginCatalog.Definitions.First(plugin => string.Equals(plugin.Id, id, StringComparison.OrdinalIgnoreCase)));

        return orderedDefinitions
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
            !string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath));
    }

    private static SteamLoaderSplashScreenSettingsData NormalizeSplashScreenSettings(
        SteamLoaderSplashScreenSettingsData? settings)
    {
        return new SteamLoaderSplashScreenSettingsData
        {
            Enabled = settings?.Enabled ?? true,
            ShowText = settings?.ShowText ?? true,
            WallpaperPath = NormalizeOptionalPath(settings?.WallpaperPath ?? string.Empty),
            IconPath = NormalizeOptionalPath(settings?.IconPath ?? string.Empty)
        };
    }

    private static int GetWindowsShellStartDelaySeconds(SteamLoaderSettingsData settings)
    {
        return ClampWindowsShellStartDelay(
            settings.WindowsShellStartDelaySeconds
            ?? settings.SplashScreen?.ExtraCloseDelaySeconds
            ?? 0);
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
            _ => SteamLoaderRuntime.StartupModeShell
        };
    }

    private static int ClampWindowsShellStartDelay(int seconds)
    {
        return Math.Clamp(seconds, 0, MaximumWindowsShellStartDelaySeconds);
    }

    private static string NormalizeUpdateChannel(string? channel)
    {
        return channel?.Trim().ToLowerInvariant() switch
        {
            SteamLoaderRuntime.UpdateChannelBeta => SteamLoaderRuntime.UpdateChannelBeta,
            _ => SteamLoaderRuntime.UpdateChannelStable
        };
    }

    private static Dictionary<string, bool> NormalizePluginStates(Dictionary<string, bool>? savedStates)
    {
        var normalized = SteamLoaderPluginCatalog.Definitions.ToDictionary(
            plugin => plugin.Id,
            plugin => plugin.DefaultEnabled,
            StringComparer.OrdinalIgnoreCase);

        if (savedStates is null)
        {
            return normalized;
        }

        foreach (var plugin in SteamLoaderPluginCatalog.Definitions)
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

    private static IReadOnlyList<string> NormalizePluginOrder(IReadOnlyList<string>? savedOrder)
    {
        var knownPluginIds = SteamLoaderPluginCatalog.Definitions
            .Select(plugin => plugin.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var canonicalPluginIds = SteamLoaderPluginCatalog.Definitions.ToDictionary(
            plugin => plugin.Id,
            plugin => plugin.Id,
            StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>(SteamLoaderPluginCatalog.Definitions.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (savedOrder is not null)
        {
            foreach (var candidate in savedOrder)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !knownPluginIds.Contains(candidate))
                {
                    continue;
                }

                var canonicalId = canonicalPluginIds[candidate];
                if (seen.Add(canonicalId))
                {
                    normalized.Add(canonicalId);
                }
            }
        }

        foreach (var plugin in SteamLoaderPluginCatalog.Definitions)
        {
            if (seen.Add(plugin.Id))
            {
                normalized.Add(plugin.Id);
            }
        }

        return normalized;
    }
    private sealed record SteamLoaderSettingsData
    {
        public bool? ConsoleModeDefaultApplied { get; init; }

        public bool? RunOnWindowsSignInUserConfigured { get; init; }

        public string? StartupMode { get; init; }

        public bool? HideWindowsShellInConsoleMode { get; init; }

        public bool? DeveloperDebugEnabled { get; init; }

        public string? UpdateChannel { get; init; }

        public bool? FirstRunCompleted { get; init; }

        public DateTimeOffset? FirstRunCompletedAtUtc { get; init; }

        public Dictionary<string, bool>? PluginEnabled { get; init; }

        public string[]? PluginOrder { get; init; }

        public int? WindowsShellStartDelaySeconds { get; init; }

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
