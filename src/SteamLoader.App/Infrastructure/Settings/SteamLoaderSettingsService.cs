using SteamLoader.App;
using SteamLoader.App.Infrastructure.Handheld;
using SteamLoader.App.Models;
using SteamLoader.App.Services;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ToolsForSteam.Splash;

namespace SteamLoader.App.Infrastructure.Settings;

public sealed class SteamLoaderSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private const int MaximumWindowsShellStartDelaySeconds = 30;
    private const string StartupModeMutexName = @"Local\ToolsForSteam.StartupMode";

    private readonly WindowsAutostartService _autostartService;
    private readonly WindowsShellService _shellService;
    private readonly IXboxModeService _xboxModeService;
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
        : this(
            autostartService,
            shellService,
            NoOpXboxModeService.Instance,
            executablePath,
            shellLaunchArguments,
            settingsPath)
    {
    }

    public SteamLoaderSettingsService(
        WindowsAutostartService autostartService,
        WindowsShellService shellService,
        IXboxModeService xboxModeService,
        string executablePath,
        string shellLaunchArguments,
        string settingsPath)
    {
        _autostartService = autostartService;
        _shellService = shellService;
        _xboxModeService = xboxModeService;
        _executablePath = executablePath;
        _shellLaunchArguments = shellLaunchArguments;
        _settingsPath = settingsPath;
    }

    public SteamLoaderGeneralSettingsSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return CreateSnapshot(LoadSettings());
        }
    }

    public SteamLoaderGeneralSettingsSnapshot EnsureDefaultConsoleModeEnabled()
    {
        lock (_gate)
        {
            var settings = LoadSettings();
            if (settings.ConsoleModeDefaultApplied == true || settings.RunOnWindowsSignInUserConfigured == true)
            {
                if (ResolveStartupMode(settings) == SteamLoaderRuntime.StartupModeXbox &&
                    !_xboxModeService.GetSupportStatus().IsSupported)
                {
                    ApplyStartupMode(SteamLoaderRuntime.StartupModeTray);
                    settings = settings with
                    {
                        StartupMode = SteamLoaderRuntime.StartupModeTray,
                        HideWindowsShellInConsoleMode = false,
                        ConsoleModeDefaultApplied = true,
                        RunOnWindowsSignInUserConfigured = true
                    };
                    SaveSettings(settings);
                }

                return CreateSnapshot(settings);
            }

            ApplyStartupMode(SteamLoaderRuntime.StartupModeShell);
            settings = settings with
            {
                StartupMode = SteamLoaderRuntime.StartupModeShell,
                ConsoleModeDefaultApplied = true,
                HideWindowsShellInConsoleMode = true
            };
            SaveSettings(settings);

            return CreateSnapshot(settings);
        }
    }

    public SteamLoaderGeneralSettingsSnapshot SetRunOnWindowsSignIn(bool enabled)
    {
        lock (_gate)
        {
            var mode = enabled ? SteamLoaderRuntime.StartupModeShell : SteamLoaderRuntime.StartupModeTray;
            ApplyStartupMode(mode);

            var settings = LoadSettings() with
            {
                StartupMode = mode,
                HideWindowsShellInConsoleMode = NormalizeStartupMode(mode) == SteamLoaderRuntime.StartupModeShell
                    ? LoadSettings().HideWindowsShellInConsoleMode ?? true
                    : false,
                ConsoleModeDefaultApplied = true,
                RunOnWindowsSignInUserConfigured = true
            };

            SaveSettings(settings);
            return CreateSnapshot(settings);
        }
    }

    public SteamLoaderGeneralSettingsSnapshot SetStartupMode(string mode)
    {
        lock (_gate)
        {
            var normalizedMode = NormalizeStartupMode(mode);
            var existingSettings = LoadSettings();
            var previousMode = ResolveStartupMode(existingSettings);
            try
            {
                ApplyStartupMode(normalizedMode);
            }
            catch
            {
                try
                {
                    ApplyStartupMode(previousMode);
                }
                catch
                {
                }

                throw;
            }

            var settings = existingSettings with
            {
                StartupMode = normalizedMode,
                HideWindowsShellInConsoleMode = normalizedMode == SteamLoaderRuntime.StartupModeShell
                    ? existingSettings.HideWindowsShellInConsoleMode ?? true
                    : false,
                ConsoleModeDefaultApplied = true,
                RunOnWindowsSignInUserConfigured = true
            };

            SaveSettings(settings);
            return CreateSnapshot(settings);
        }
    }

    private void ApplyStartupMode(string mode)
    {
        var normalizedMode = NormalizeStartupMode(mode);
        if (normalizedMode == SteamLoaderRuntime.StartupModeXbox)
        {
            var support = _xboxModeService.GetSupportStatus();
            if (!support.IsSupported)
            {
                throw new InvalidOperationException(support.Reason);
            }
        }

        using var modeMutex = new Mutex(false, StartupModeMutexName);
        var lockTaken = false;
        try
        {
            try
            {
                lockTaken = modeMutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                lockTaken = true;
            }

            if (!lockTaken)
            {
                throw new TimeoutException("Another Tools for Steam process is already changing the startup mode.");
            }

            _autostartService.DisableSteamAutostartEntries();
            _autostartService.SetEnabled(_executablePath, SteamLoaderRuntime.AutostartArguments, false);

            switch (normalizedMode)
            {
                case SteamLoaderRuntime.StartupModeShell:
                    _xboxModeService.SetStartupEnabled(false);
                    _shellService.SetEnabled(_executablePath, _shellLaunchArguments, true);
                    break;
                case SteamLoaderRuntime.StartupModeTray:
                    _xboxModeService.SetStartupEnabled(false);
                    _shellService.SetExplorerShell();
                    _autostartService.SetEnabled(_executablePath, SteamLoaderRuntime.AutostartArguments, true);
                    break;
                case SteamLoaderRuntime.StartupModeXbox:
                    _shellService.SetExplorerShell();
                    _xboxModeService.SetStartupEnabled(true);
                    break;
                default:
                    _xboxModeService.SetStartupEnabled(false);
                    _shellService.SetEnabled(_executablePath, _shellLaunchArguments, true);
                    break;
            }

            VerifyStartupMode(normalizedMode);
        }
        finally
        {
            if (lockTaken)
            {
                modeMutex.ReleaseMutex();
            }
        }
    }

    private void VerifyStartupMode(string mode)
    {
        var shellEnabled = _shellService.IsEnabled(_executablePath, _shellLaunchArguments);
        var explorerEnabled = string.Equals(_shellService.GetShellCommand(), "explorer.exe", StringComparison.OrdinalIgnoreCase);
        var trayEnabled = _autostartService.IsEnabled(_executablePath, SteamLoaderRuntime.AutostartArguments);
        var valid = mode switch
        {
            SteamLoaderRuntime.StartupModeShell => shellEnabled && !trayEnabled && _xboxModeService.VerifyStartupEnabled(false),
            SteamLoaderRuntime.StartupModeTray => explorerEnabled && trayEnabled && _xboxModeService.VerifyStartupEnabled(false),
            SteamLoaderRuntime.StartupModeXbox => explorerEnabled && !trayEnabled && _xboxModeService.VerifyStartupEnabled(true),
            _ => false
        };

        if (!valid)
        {
            throw new InvalidOperationException($"Windows did not confirm a clean transition to {mode} startup mode.");
        }
    }

    public SteamLoaderGeneralSettingsSnapshot SetHideWindowsShellInConsoleMode(bool enabled)
    {
        lock (_gate)
        {
            var settings = LoadSettings();
            var startupMode = ResolveStartupMode(settings);
            var allowHideWindowsShell = string.Equals(
                startupMode,
                SteamLoaderRuntime.StartupModeShell,
                StringComparison.OrdinalIgnoreCase);

            settings = settings with
            {
                HideWindowsShellInConsoleMode = allowHideWindowsShell && enabled
            };

            SaveSettings(settings);
            return CreateSnapshot(settings);
        }
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

    public SteamLoaderGeneralSettingsSnapshot SetSplashScreenArtworkMode(string mode)
    {
        var settings = LoadSettings();
        var splashScreen = NormalizeSplashScreenSettings(settings.SplashScreen);
        var normalizedMode = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedMode is not StartupSplashArtworkMode.Dynamic and not StartupSplashArtworkMode.Custom)
        {
            throw new InvalidOperationException("Splash artwork mode must be dynamic or custom.");
        }

        if (normalizedMode == StartupSplashArtworkMode.Custom &&
            !IsSupportedCustomSplashImage(splashScreen.CustomImagePath))
        {
            throw new InvalidOperationException("Choose an existing PNG, JPG, JPEG, or WebP image before enabling the custom splash.");
        }

        SaveSettings(settings with
        {
            SplashScreen = splashScreen with { ArtworkMode = normalizedMode }
        });
        return GetSnapshot();
    }

    public SteamLoaderGeneralSettingsSnapshot SetSplashScreenCustomImagePath(string? path)
    {
        var settings = LoadSettings();
        var normalizedPath = NormalizeOptionalPath(path);
        if (!string.IsNullOrWhiteSpace(normalizedPath) && !IsSupportedCustomSplashImage(normalizedPath))
        {
            throw new InvalidOperationException("The custom splash must be an existing PNG, JPG, JPEG, or WebP image.");
        }

        var splashScreen = NormalizeSplashScreenSettings(settings.SplashScreen) with
        {
            ArtworkMode = string.IsNullOrWhiteSpace(normalizedPath)
                ? StartupSplashArtworkMode.Dynamic
                : StartupSplashArtworkMode.Custom,
            CustomImagePath = normalizedPath
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

    public ControllerShortcutSettingsSnapshot GetControllerShortcutSettings()
    {
        lock (_gate)
        {
            return BuildControllerShortcutSettings(LoadSettings());
        }
    }

    public SteamLoaderGeneralSettingsSnapshot SetControllerShortcutButton(string key, string buttonId)
    {
        lock (_gate)
        {
            if (!ControllerShortcutSettingsSnapshot.IsSupportedButton(buttonId))
            {
                throw new InvalidOperationException("Choose a supported controller button.");
            }

            var settings = LoadSettings();
            var shortcuts = BuildControllerShortcutSettings(settings);
            var combination = new[] { ControllerShortcutSettingsSnapshot.NormalizeButton(
                buttonId,
                key?.Trim().Equals("in-game", StringComparison.OrdinalIgnoreCase) == true
                    ? ControllerShortcutSettingsSnapshot.DefaultInGameButton
                    : ControllerShortcutSettingsSnapshot.DefaultSteamButton) };
            shortcuts = key?.Trim().ToLowerInvariant() switch
            {
                "steam" => shortcuts with
                {
                    SteamMenuButtons = combination,
                    SteamQuickAccessButtons = combination
                },
                "in-game" => shortcuts with
                {
                    InGameOverlayButtons = combination,
                    InGameQuickAccessButtons = combination
                },
                _ => throw new InvalidOperationException("The overlay button setting is unknown.")
            };

            SaveSettings(settings with { ControllerShortcuts = ToData(shortcuts) });
            return GetSnapshot();
        }
    }

    public SteamLoaderGeneralSettingsSnapshot SetControllerShortcutCombination(
        string action,
        IReadOnlyList<string>? buttonIds)
    {
        lock (_gate)
        {
            var suppliedButtons = (buttonIds ?? [])
                .Where(buttonId => !string.IsNullOrWhiteSpace(buttonId))
                .ToArray();
            if (suppliedButtons.Length is < 1 or > ControllerShortcutSettingsSnapshot.MaximumButtonsPerCombination ||
                suppliedButtons.Any(buttonId => !ControllerShortcutSettingsSnapshot.IsSupportedButton(buttonId)) ||
                suppliedButtons.Distinct(StringComparer.OrdinalIgnoreCase).Count() != suppliedButtons.Length)
            {
                throw new InvalidOperationException(
                    $"Choose between 1 and {ControllerShortcutSettingsSnapshot.MaximumButtonsPerCombination} different supported controller buttons.");
            }

            var settings = LoadSettings();
            var shortcuts = BuildControllerShortcutSettings(settings);
            var normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
            var fallback = normalizedAction.StartsWith("in-game", StringComparison.Ordinal)
                ? new[] { ControllerShortcutSettingsSnapshot.DefaultInGameButton }
                : new[] { ControllerShortcutSettingsSnapshot.DefaultSteamButton };
            var combination = ControllerShortcutSettingsSnapshot.NormalizeCombination(suppliedButtons, fallback);
            shortcuts = normalizedAction switch
            {
                "steam-menu" => shortcuts with { SteamMenuButtons = combination },
                "steam-quick-access" => shortcuts with { SteamQuickAccessButtons = combination },
                "in-game-overlay" => shortcuts with { InGameOverlayButtons = combination },
                "in-game-quick-access" => shortcuts with { InGameQuickAccessButtons = combination },
                _ => throw new InvalidOperationException("The overlay action is unknown.")
            };

            SaveSettings(settings with { ControllerShortcuts = ToData(shortcuts) });
            return GetSnapshot();
        }
    }

    public SteamLoaderGeneralSettingsSnapshot SetControllerShortcutHoldMilliseconds(string key, int milliseconds)
    {
        lock (_gate)
        {
            var settings = LoadSettings();
            var shortcuts = BuildControllerShortcutSettings(settings);
            shortcuts = key?.Trim().ToLowerInvariant() switch
            {
                "steam" => shortcuts with { SteamHoldMilliseconds = milliseconds },
                "in-game-overlay" => shortcuts with { InGameOverlayHoldMilliseconds = milliseconds },
                "in-game-quick-access" => shortcuts with { InGameQuickAccessHoldMilliseconds = milliseconds },
                _ => throw new InvalidOperationException("The overlay hold-time setting is unknown.")
            };
            shortcuts = ControllerShortcutSettingsSnapshot.Normalize(
                shortcuts.SteamMenuButtons,
                shortcuts.SteamQuickAccessButtons,
                shortcuts.InGameOverlayButtons,
                shortcuts.InGameQuickAccessButtons,
                legacySteamButton: null,
                legacyInGameButton: null,
                steamHoldMilliseconds: shortcuts.SteamHoldMilliseconds,
                inGameOverlayHoldMilliseconds: shortcuts.InGameOverlayHoldMilliseconds,
                inGameQuickAccessHoldMilliseconds: shortcuts.InGameQuickAccessHoldMilliseconds);

            SaveSettings(settings with { ControllerShortcuts = ToData(shortcuts) });
            return GetSnapshot();
        }
    }

    public SteamLoaderGeneralSettingsSnapshot ResetControllerShortcutSettings()
    {
        lock (_gate)
        {
            var settings = LoadSettings() with
            {
                ControllerShortcuts = ToData(ControllerShortcutSettingsSnapshot.Default)
            };
            SaveSettings(settings);
            return CreateSnapshot(settings);
        }
    }

    internal static ControllerShortcutSettingsSnapshot ReadControllerShortcutSettingsFile(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return ControllerShortcutSettingsSnapshot.Default;
            }

            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<SteamLoaderSettingsData>(json, JsonOptions)
                ?? new SteamLoaderSettingsData();
            return BuildControllerShortcutSettings(settings);
        }
        catch
        {
            return ControllerShortcutSettingsSnapshot.Default;
        }
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
            return false;
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
        lock (_gate)
        {
            var settings = LoadSettings();
            return ShouldHideWindowsShellInConsoleMode(ResolveStartupMode(settings), settings);
        }
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
        var customImagePath = splashScreen.CustomImagePath ?? string.Empty;

        return new SteamLoaderSplashScreenSettingsSnapshot(
            splashScreen.ArtworkMode ?? StartupSplashArtworkMode.Dynamic,
            customImagePath,
            IsSupportedCustomSplashImage(customImagePath));
    }

    private static ControllerShortcutSettingsSnapshot BuildControllerShortcutSettings(
        SteamLoaderSettingsData settings)
    {
        var shortcuts = settings.ControllerShortcuts;
        return ControllerShortcutSettingsSnapshot.Normalize(
            shortcuts?.SteamMenuButtons,
            shortcuts?.SteamQuickAccessButtons,
            shortcuts?.InGameOverlayButtons,
            shortcuts?.InGameQuickAccessButtons,
            shortcuts?.SteamButton,
            shortcuts?.InGameButton,
            shortcuts?.SteamHoldMilliseconds,
            shortcuts?.InGameOverlayHoldMilliseconds,
            shortcuts?.InGameQuickAccessHoldMilliseconds);
    }

    private static SteamLoaderControllerShortcutSettingsData ToData(
        ControllerShortcutSettingsSnapshot settings)
    {
        return new SteamLoaderControllerShortcutSettingsData
        {
            SteamMenuButtons = settings.SteamMenuButtons.ToArray(),
            SteamQuickAccessButtons = settings.SteamQuickAccessButtons.ToArray(),
            InGameOverlayButtons = settings.InGameOverlayButtons.ToArray(),
            InGameQuickAccessButtons = settings.InGameQuickAccessButtons.ToArray(),
            SteamHoldMilliseconds = settings.SteamHoldMilliseconds,
            InGameOverlayHoldMilliseconds = settings.InGameOverlayHoldMilliseconds,
            InGameQuickAccessHoldMilliseconds = settings.InGameQuickAccessHoldMilliseconds
        };
    }

    private static SteamLoaderSplashScreenSettingsData NormalizeSplashScreenSettings(
        SteamLoaderSplashScreenSettingsData? settings)
    {
        var customImagePath = NormalizeOptionalPath(
            settings?.CustomImagePath ?? settings?.WallpaperPath ?? string.Empty);

        return new SteamLoaderSplashScreenSettingsData
        {
            ArtworkMode = StartupSplashArtworkMode.Normalize(settings?.ArtworkMode, customImagePath),
            CustomImagePath = customImagePath
        };
    }

    private static bool IsSupportedCustomSplashImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        return Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp";
    }

    private static int GetWindowsShellStartDelaySeconds(SteamLoaderSettingsData settings)
    {
        return ClampWindowsShellStartDelay(
            settings.WindowsShellStartDelaySeconds
            ?? settings.SplashScreen?.ExtraCloseDelaySeconds
            ?? 0);
    }

    private static bool ShouldHideWindowsShellInConsoleMode(string startupMode, SteamLoaderSettingsData settings)
    {
        return string.Equals(startupMode, SteamLoaderRuntime.StartupModeShell, StringComparison.OrdinalIgnoreCase)
            && settings.HideWindowsShellInConsoleMode != false;
    }

    private SteamLoaderGeneralSettingsSnapshot CreateSnapshot(SteamLoaderSettingsData settings)
    {
        var startupMode = ResolveStartupMode(settings);
        var xboxModeSupport = _xboxModeService.GetSupportStatus();
        var handheld = HandheldDeviceCatalog.Detect();
        return new SteamLoaderGeneralSettingsSnapshot(
            RunOnWindowsSignIn: true,
            StartupMode: startupMode,
            HideWindowsShellInConsoleMode: ShouldHideWindowsShellInConsoleMode(startupMode, settings),
            FirstRunCompleted: settings.FirstRunCompleted == true,
            ConsoleModeDefaultApplied: settings.ConsoleModeDefaultApplied == true,
            SplashScreen: BuildSplashScreenSettings(settings),
            ControllerShortcuts: BuildControllerShortcutSettings(settings),
            WindowsShellStartDelaySeconds: GetWindowsShellStartDelaySeconds(settings),
            ProductVersion: GetProductVersion(),
            InstallPath: AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            DeveloperDebugEnabled: settings.DeveloperDebugEnabled == true,
            XboxModeSupported: xboxModeSupport.IsSupported,
            XboxModeSupportReason: xboxModeSupport.Reason,
            HandheldPerformanceAvailable: HandheldDeviceCatalog.IsSupported(handheld),
            HandheldPerformanceTitle: handheld.DisplayName,
            OemSoftwareAvailable: HandheldDeviceCatalog.IsSupported(handheld) && handheld.OemSoftware.Supported,
            OemSoftwareTitle: handheld.OemSoftware.SoftwareName,
            Plugins: BuildPluginStates(settings));
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
            SteamLoaderRuntime.StartupModeXbox => SteamLoaderRuntime.StartupModeXbox,
            "external" => SteamLoaderRuntime.StartupModeXbox,
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

        public SteamLoaderControllerShortcutSettingsData? ControllerShortcuts { get; init; }
    }

    private sealed record SteamLoaderControllerShortcutSettingsData
    {
        public string[]? SteamMenuButtons { get; init; }

        public string[]? SteamQuickAccessButtons { get; init; }

        public string[]? InGameOverlayButtons { get; init; }

        public string[]? InGameQuickAccessButtons { get; init; }

        // Legacy single-button fields are read for automatic migration.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SteamButton { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? InGameButton { get; init; }

        public int? SteamHoldMilliseconds { get; init; }

        public int? InGameOverlayHoldMilliseconds { get; init; }

        public int? InGameQuickAccessHoldMilliseconds { get; init; }
    }

    private sealed record SteamLoaderSplashScreenSettingsData
    {
        public string? ArtworkMode { get; init; }

        public string? CustomImagePath { get; init; }

        // Legacy fields are read for a seamless migration and are omitted as
        // soon as the splash configuration is saved in the new format.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Enabled { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ShowText { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? WallpaperPath { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? IconPath { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? ExtraCloseDelaySeconds { get; init; }
    }
}
