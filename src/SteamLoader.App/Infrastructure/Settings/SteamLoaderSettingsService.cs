using SteamLoader.App;
using SteamLoader.App.Infrastructure.Handheld;
using SteamLoader.App.Infrastructure.Themes;
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
    internal const int DefaultNotificationScalePercent = 100;
    internal const int MinimumNotificationScalePercent = 100;
    internal const int MaximumNotificationScalePercent = 250;
    internal const int NotificationScaleStepPercent = 25;
    private const string StartupModeMutexName = @"Local\ToolsForSteam.StartupMode";
    private const string StoreSyncPluginId = "store-sync";
    private const string OmniLibraryPluginId = "omnilibrary";

    private readonly WindowsAutostartService _autostartService;
    private readonly WindowsShellService _shellService;
    private readonly LegacyXboxModeCleanup _legacyXboxModeCleanup;
    private readonly IConsoleStartTask _consoleStartTask;
    private readonly string _executablePath;
    private readonly string _settingsPath;
    private readonly object _gate = new();

    // shellLaunchArguments is no longer used: Shell Takeover was removed and a
    // leftover Winlogon value is recognized by WindowsShellService itself. The
    // parameter stays so existing callers keep compiling.
    public SteamLoaderSettingsService(
        WindowsAutostartService autostartService,
        WindowsShellService shellService,
        string executablePath,
        string shellLaunchArguments,
        string settingsPath)
        : this(
            autostartService,
            shellService,
            new LegacyXboxModeCleanup(),
            new ConsoleStartTaskService(executablePath),
            executablePath,
            shellLaunchArguments,
            settingsPath)
    {
    }

    internal SteamLoaderSettingsService(
        WindowsAutostartService autostartService,
        WindowsShellService shellService,
        LegacyXboxModeCleanup legacyXboxModeCleanup,
        IConsoleStartTask consoleStartTask,
        string executablePath,
        string shellLaunchArguments,
        string settingsPath)
    {
        _autostartService = autostartService;
        _shellService = shellService;
        _legacyXboxModeCleanup = legacyXboxModeCleanup;
        _consoleStartTask = consoleStartTask;
        _executablePath = executablePath;
        _ = shellLaunchArguments;
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
            if (settings.HideWindowsShellDefaultOffApplied != true)
            {
                // Older builds stored "hide" as their default, not as a user
                // choice. Switch it off once; a later choice in the QAM stays.
                settings = settings with
                {
                    HideWindowsShellInConsoleMode = false,
                    HideWindowsShellDefaultOffApplied = true
                };
                SaveSettings(settings);
                SteamStartupDiagnostics.Write("hide Windows shell in Console Mode set to its new default (off)");
            }

            var legacyXboxMode = IsLegacyXboxStartupMode(settings.StartupMode);
            var legacyShellMode = IsLegacyShellStartupMode(settings.StartupMode) || _shellService.IsLegacyTfsShell();
            if (legacyXboxMode || legacyShellMode)
            {
                // Xbox Mode and Shell Takeover were removed. Both meant "TFS as
                // the console start", which Console Mode provides with Explorer
                // as the Windows shell. Point Windows away from the removed
                // Gaming Home package first, without leaving a running session.
                if (legacyXboxMode)
                {
                    _legacyXboxModeCleanup.Run(leaveActiveSession: false);
                }

                var targetMode = string.Equals(
                    NormalizeStartupMode(settings.StartupMode),
                    SteamLoaderRuntime.StartupModeTray,
                    StringComparison.Ordinal)
                    ? SteamLoaderRuntime.StartupModeTray
                    : SteamLoaderRuntime.StartupModeConsole;
                var migratedMode = TryApplyStartupModeWithTrayFallback(targetMode);
                if (migratedMode is null)
                {
                    // Keep the stored value so the next start retries; a failed
                    // migration must never stop TFS from starting.
                    return CreateSnapshot(settings);
                }

                SteamStartupDiagnostics.Write(
                    $"removed startup mode (xbox={legacyXboxMode} shell={legacyShellMode}) migrated to {migratedMode}");
                settings = settings with
                {
                    StartupMode = migratedMode,
                    HideWindowsShellInConsoleMode = SteamLoaderRuntime.IsConsoleStartupMode(migratedMode) &&
                        settings.HideWindowsShellInConsoleMode == true,
                    ConsoleModeDefaultApplied = true,
                    RunOnWindowsSignInUserConfigured = true
                };
                SaveSettings(settings);
                return CreateSnapshot(settings);
            }

            if (settings.ConsoleModeDefaultApplied == true || settings.RunOnWindowsSignInUserConfigured == true)
            {
                RepairDisabledConsoleStartTask(settings);
                return CreateSnapshot(settings);
            }

            var defaultMode = TryApplyStartupModeWithTrayFallback(SteamLoaderRuntime.StartupModeConsole);
            if (defaultMode is null)
            {
                return CreateSnapshot(settings);
            }

            settings = settings with
            {
                StartupMode = defaultMode,
                ConsoleModeDefaultApplied = true,
                HideWindowsShellInConsoleMode = false,
                HideWindowsShellDefaultOffApplied = true
            };
            SaveSettings(settings);

            return CreateSnapshot(settings);
        }
    }

    /// <summary>
    /// Older builds' autostart cleanup disabled TFS' own Console Mode sign-in
    /// task, so Console Mode silently stopped starting. Re-enable it when it
    /// belongs to this executable. A task of another TFS build (for example a
    /// dev build next to the installed one) is left alone. Never throws.
    /// </summary>
    private void RepairDisabledConsoleStartTask(SteamLoaderSettingsData settings)
    {
        if (!SteamLoaderRuntime.IsConsoleStartupMode(ResolveStartupMode(settings)))
        {
            return;
        }

        try
        {
            if (!_consoleStartTask.IsDisabledForThisExecutable())
            {
                return;
            }

            _consoleStartTask.Register();
            SteamStartupDiagnostics.Write("re-enabled the disabled Console Mode sign-in task");
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write($"disabled Console Mode sign-in task could not be repaired: {exception.Message}");
        }
    }

    /// <summary>
    /// Applies a startup mode; if Console Mode's sign-in task cannot be created,
    /// falls back to eTray so TFS still starts at sign-in. Never throws.
    /// </summary>
    private string? TryApplyStartupModeWithTrayFallback(string mode)
    {
        try
        {
            ApplyStartupMode(mode);
            return mode;
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write($"startup mode {mode} could not be applied: {exception.Message}");
        }

        if (!string.Equals(mode, SteamLoaderRuntime.StartupModeTray, StringComparison.Ordinal))
        {
            try
            {
                ApplyStartupMode(SteamLoaderRuntime.StartupModeTray);
                return SteamLoaderRuntime.StartupModeTray;
            }
            catch (Exception exception)
            {
                SteamStartupDiagnostics.Write($"eTray fallback could not be applied: {exception.Message}");
            }
        }

        // Last line of defense: never leave Windows starting a removed shell mode.
        try
        {
            if (_shellService.IsLegacyTfsShell())
            {
                _shellService.SetExplorerShell();
            }
        }
        catch
        {
        }

        return null;
    }

    public SteamLoaderGeneralSettingsSnapshot SetRunOnWindowsSignIn(bool enabled)
    {
        lock (_gate)
        {
            var mode = enabled ? SteamLoaderRuntime.StartupModeConsole : SteamLoaderRuntime.StartupModeTray;
            ApplyStartupMode(mode);

            var settings = LoadSettings() with
            {
                StartupMode = mode,
                HideWindowsShellInConsoleMode = SteamLoaderRuntime.IsConsoleStartupMode(mode) &&
                    LoadSettings().HideWindowsShellInConsoleMode == true,
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
                // Hiding is off by default. A switch from eTray starts with it
                // off; staying in Console Mode keeps the user's own choice.
                HideWindowsShellInConsoleMode = SteamLoaderRuntime.IsConsoleStartupMode(normalizedMode) &&
                    SteamLoaderRuntime.IsConsoleStartupMode(previousMode) &&
                    existingSettings.HideWindowsShellInConsoleMode == true,
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
                case SteamLoaderRuntime.StartupModeTray:
                    TryRemoveConsoleStartTask();
                    _shellService.SetExplorerShell();
                    _autostartService.SetEnabled(_executablePath, SteamLoaderRuntime.AutostartArguments, true);
                    break;
                default:
                    // Console Mode. Explorer first: if the sign-in task cannot be
                    // created, the caller re-applies the previous mode and Windows
                    // still boots to a working desktop in between.
                    _shellService.SetExplorerShell();
                    _consoleStartTask.Register();
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
        var explorerEnabled = string.Equals(_shellService.GetShellCommand(), "explorer.exe", StringComparison.OrdinalIgnoreCase);
        var trayEnabled = _autostartService.IsEnabled(_executablePath, SteamLoaderRuntime.AutostartArguments);
        var valid = mode switch
        {
            SteamLoaderRuntime.StartupModeTray => explorerEnabled && trayEnabled,
            SteamLoaderRuntime.StartupModeConsole => explorerEnabled && !trayEnabled && _consoleStartTask.IsRegistered(),
            _ => false
        };

        if (!valid)
        {
            throw new InvalidOperationException($"Windows did not confirm a clean transition to {mode} startup mode.");
        }
    }

    private void TryRemoveConsoleStartTask()
    {
        try
        {
            _consoleStartTask.Unregister();
        }
        catch (Exception exception)
        {
            // Never block a switch to eTray on Task Scheduler:
            // a leftover task only starts TFS as a plain tray launch, because
            // Console Mode is no longer the saved mode.
            SteamStartupDiagnostics.Write($"console start task could not be removed: {exception.Message}");
        }
    }

    /// <summary>
    /// Uninstall: remove every startup hook that points at this TFS install
    /// and give Windows back its own shell.
    /// </summary>
    public void RemoveStartupIntegration()
    {
        lock (_gate)
        {
            TryRemoveConsoleStartTask();

            try
            {
                _autostartService.SetEnabled(_executablePath, SteamLoaderRuntime.AutostartArguments, false);
            }
            catch (Exception exception)
            {
                SteamStartupDiagnostics.Write($"uninstall could not remove the TFS Run value: {exception.Message}");
            }

            try
            {
                _shellService.RestoreIfOwnedBy(_executablePath);
            }
            catch (Exception exception)
            {
                SteamStartupDiagnostics.Write($"uninstall could not restore the Windows shell: {exception.Message}");
            }
        }
    }

    public SteamLoaderGeneralSettingsSnapshot SetHideWindowsShellInConsoleMode(bool enabled)
    {
        lock (_gate)
        {
            var settings = LoadSettings();
            var startupMode = ResolveStartupMode(settings);
            var allowHideWindowsShell = SteamLoaderRuntime.IsConsoleStartupMode(startupMode);

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

    public SteamLoaderGeneralSettingsSnapshot SetWallpaperEnabled(bool enabled)
    {
        var settings = LoadSettings();
        var theme = settings.Theme ?? new SteamLoaderThemeSettingsData();

        if (enabled)
        {
            var previousWallpaperPath = WallpaperService.ApplyTfsWallpaper();
            theme = theme with
            {
                WallpaperEnabled = true,
                PreviousWallpaperPath = theme.PreviousWallpaperPath ?? previousWallpaperPath
            };
        }
        else
        {
            WallpaperService.RestoreWallpaper(theme.PreviousWallpaperPath);
            theme = theme with
            {
                WallpaperEnabled = false,
                PreviousWallpaperPath = null
            };
        }

        SaveSettings(settings with { Theme = theme });
        return GetSnapshot();
    }

    public SteamLoaderGeneralSettingsSnapshot SetAnimationsEnabled(bool enabled)
    {
        var settings = LoadSettings();
        var theme = settings.Theme ?? new SteamLoaderThemeSettingsData();
        SaveSettings(settings with { Theme = theme with { AnimationsEnabled = enabled } });
        return GetSnapshot();
    }

    public SteamLoaderGeneralSettingsSnapshot SetAccentColor(string color)
    {
        var settings = LoadSettings();
        var theme = settings.Theme ?? new SteamLoaderThemeSettingsData();
        SaveSettings(settings with { Theme = theme with { AccentColor = NormalizeAccentColor(color) } });
        return GetSnapshot();
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

    /// <summary>
    /// Game Bar controller shortcut automation (like Playhub): the Xbox
    /// button does not open Game Bar in Big Picture, only in Xbox / Microsoft
    /// Store games. On unless the user turned it off.
    /// </summary>
    public bool IsGameBarControllerAutomationEnabled()
    {
        return LoadSettings().GameBarControllerAutomation != false;
    }

    public SteamLoaderGeneralSettingsSnapshot SetGameBarControllerAutomation(bool enabled)
    {
        lock (_gate)
        {
            var settings = LoadSettings() with { GameBarControllerAutomation = enabled };
            SaveSettings(settings);
            return CreateSnapshot(settings);
        }
    }

    public int GetNotificationScalePercent()
    {
        return NormalizeNotificationScalePercent(LoadSettings().NotificationScalePercent);
    }

    public SteamLoaderGeneralSettingsSnapshot SetNotificationScalePercent(int percent)
    {
        lock (_gate)
        {
            var settings = LoadSettings() with
            {
                NotificationScalePercent = NormalizeNotificationScalePercent(percent)
            };

            SaveSettings(settings);
            return CreateSnapshot(settings);
        }
    }

    /// <summary>
    /// Clamps to 100–250 % and snaps to the 25 % steps the settings page offers.
    /// </summary>
    internal static int NormalizeNotificationScalePercent(int? percent)
    {
        var value = Math.Clamp(percent ?? DefaultNotificationScalePercent, MinimumNotificationScalePercent, MaximumNotificationScalePercent);
        var snapped = (int)Math.Round(value / (double)NotificationScaleStepPercent, MidpointRounding.AwayFromZero) * NotificationScaleStepPercent;
        return Math.Clamp(snapped, MinimumNotificationScalePercent, MaximumNotificationScalePercent);
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
        lock (_gate)
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

            if (enabled &&
                TryGetAlternativeLibraryPluginId(definition.Id, out var alternativePluginId))
            {
                pluginStates[alternativePluginId] = false;
            }

            SaveSettings(settings with
            {
                PluginEnabled = pluginStates
            });

            return CreateSnapshot(settings with { PluginEnabled = pluginStates });
        }
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
        return pluginStates.TryGetValue(definition.Id, out var enabled)
            ? enabled
            : definition.DefaultEnabled;
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

    /// <summary>True until the controller intro video was shown once (or skipped).</summary>
    public bool IsIntroVideoPending()
    {
        return LoadSettings().IntroVideoSeen != true;
    }

    public void MarkIntroVideoSeen()
    {
        lock (_gate)
        {
            var settings = LoadSettings();
            if (settings.IntroVideoSeen == true)
            {
                return;
            }

            SaveSettings(settings with
            {
                IntroVideoSeen = true,
                IntroVideoSeenAtUtc = DateTimeOffset.UtcNow
            });
        }
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

    private static readonly IReadOnlyList<string> AccentColorPalette =
    [
        "#66c0f4",
        "#5ee6a8",
        "#c084fc",
        "#ff9f43",
        "#ff6b6b",
        "#4dd0e1",
    ];
    private const string NoneAccentColor = "none";

    private static string NormalizeAccentColor(string? color)
    {
        if (string.Equals(color, NoneAccentColor, StringComparison.OrdinalIgnoreCase))
        {
            return NoneAccentColor;
        }

        return AccentColorPalette.FirstOrDefault(candidate =>
            string.Equals(candidate, color, StringComparison.OrdinalIgnoreCase)) ?? NoneAccentColor;
    }

    private static SteamLoaderThemeSettingsSnapshot BuildThemeSettings(SteamLoaderSettingsData settings)
    {
        return new SteamLoaderThemeSettingsSnapshot(
            settings.Theme?.WallpaperEnabled == true,
            settings.Theme?.AnimationsEnabled != false,
            NormalizeAccentColor(settings.Theme?.AccentColor));
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
        // luis (2026-11-13): hiding the Windows shell is off unless the user
        // turns it on; a missing value means off.
        return SteamLoaderRuntime.IsConsoleStartupMode(startupMode)
            && settings.HideWindowsShellInConsoleMode == true;
    }

    private SteamLoaderGeneralSettingsSnapshot CreateSnapshot(SteamLoaderSettingsData settings)
    {
        var startupMode = ResolveStartupMode(settings);
        var handheld = HandheldDeviceCatalog.Detect();
        return new SteamLoaderGeneralSettingsSnapshot(
            RunOnWindowsSignIn: true,
            StartupMode: startupMode,
            HideWindowsShellInConsoleMode: ShouldHideWindowsShellInConsoleMode(startupMode, settings),
            FirstRunCompleted: settings.FirstRunCompleted == true,
            ConsoleModeDefaultApplied: settings.ConsoleModeDefaultApplied == true,
            SplashScreen: BuildSplashScreenSettings(settings),
            Theme: BuildThemeSettings(settings),
            ControllerShortcuts: BuildControllerShortcutSettings(settings),
            WindowsShellStartDelaySeconds: GetWindowsShellStartDelaySeconds(settings),
            ProductVersion: GetProductVersion(),
            InstallPath: AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            DeveloperDebugEnabled: settings.DeveloperDebugEnabled == true,
            HandheldPerformanceAvailable: HandheldDeviceCatalog.IsSupported(handheld),
            HandheldPerformanceTitle: handheld.DisplayName,
            OemSoftwareAvailable: HandheldDeviceCatalog.IsSupported(handheld) && handheld.OemSoftware.Supported,
            OemSoftwareTitle: handheld.OemSoftware.SoftwareName,
            Plugins: BuildPluginStates(settings),
            NotificationScalePercent: NormalizeNotificationScalePercent(settings.NotificationScalePercent),
            GameBarControllerAutomation: settings.GameBarControllerAutomation != false,
            WindowsXboxModeConflict: SteamLoaderRuntime.IsConsoleStartupMode(startupMode) && XboxModeDetector.IsEnabled());
    }

    /// <summary>Reads Windows' own Xbox Mode; replaceable in tests.</summary>
    internal WindowsXboxModeDetector XboxModeDetector { get; init; } = new();

    private static string NormalizeOptionalPath(string? path)
    {
        return (path ?? string.Empty).Trim().Trim('"');
    }

    private string ResolveStartupMode(SteamLoaderSettingsData settings)
    {
        if (_autostartService.IsEnabled(_executablePath, SteamLoaderRuntime.AutostartArguments))
        {
            return SteamLoaderRuntime.StartupModeTray;
        }

        return NormalizeStartupMode(settings.StartupMode);
    }

    private static string NormalizeStartupMode(string? mode)
    {
        // Anything else, including the removed "shell" (Shell Takeover) and
        // "xbox"/"external" (Xbox Mode) values, resolves to Console Mode.
        return mode?.Trim().ToLowerInvariant() switch
        {
            SteamLoaderRuntime.StartupModeTray => SteamLoaderRuntime.StartupModeTray,
            _ => SteamLoaderRuntime.StartupModeConsole
        };
    }

    public static bool IsSupportedStartupMode(string? mode)
    {
        var normalized = mode?.Trim().ToLowerInvariant();
        return normalized is SteamLoaderRuntime.StartupModeTray
            or SteamLoaderRuntime.StartupModeConsole;
    }

    private static bool IsLegacyXboxStartupMode(string? mode)
    {
        var normalized = mode?.Trim().ToLowerInvariant();
        return normalized is "xbox" or "external";
    }

    private static bool IsLegacyShellStartupMode(string? mode)
    {
        return string.Equals(mode?.Trim(), SteamLoaderRuntime.LegacyStartupModeShell, StringComparison.OrdinalIgnoreCase);
    }

    private static int ClampWindowsShellStartDelay(int seconds)
    {
        return Math.Clamp(seconds, 0, MaximumWindowsShellStartDelaySeconds);
    }

    private static Dictionary<string, bool> NormalizePluginStates(Dictionary<string, bool>? savedStates)
    {
        var normalized = SteamLoaderPluginCatalog.Definitions.ToDictionary(
            plugin => plugin.Id,
            plugin => plugin.DefaultEnabled,
            StringComparer.OrdinalIgnoreCase);

        if (savedStates is not null)
        {
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
        }

        if (normalized[StoreSyncPluginId] && normalized[OmniLibraryPluginId])
        {
            normalized[OmniLibraryPluginId] = false;
        }

        return normalized;
    }

    private static bool TryGetAlternativeLibraryPluginId(string pluginId, out string alternativePluginId)
    {
        if (string.Equals(pluginId, StoreSyncPluginId, StringComparison.OrdinalIgnoreCase))
        {
            alternativePluginId = OmniLibraryPluginId;
            return true;
        }

        if (string.Equals(pluginId, OmniLibraryPluginId, StringComparison.OrdinalIgnoreCase))
        {
            alternativePluginId = StoreSyncPluginId;
            return true;
        }

        alternativePluginId = string.Empty;
        return false;
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

        // Set once the old "hide by default" value was switched off (2026-11-13).
        public bool? HideWindowsShellDefaultOffApplied { get; init; }

        public bool? DeveloperDebugEnabled { get; init; }

        public bool? FirstRunCompleted { get; init; }

        public DateTimeOffset? FirstRunCompletedAtUtc { get; init; }

        // Controller intro video: shown once in Big Picture, then only on request.
        public bool? IntroVideoSeen { get; init; }

        public DateTimeOffset? IntroVideoSeenAtUtc { get; init; }

        public Dictionary<string, bool>? PluginEnabled { get; init; }

        public string[]? PluginOrder { get; init; }

        public int? WindowsShellStartDelaySeconds { get; init; }

        public int? NotificationScalePercent { get; init; }

        public bool? GameBarControllerAutomation { get; init; }

        public SteamLoaderSplashScreenSettingsData? SplashScreen { get; init; }

        public SteamLoaderThemeSettingsData? Theme { get; init; }

        public SteamLoaderControllerShortcutSettingsData? ControllerShortcuts { get; init; }
    }

    private sealed record SteamLoaderThemeSettingsData
    {
        public bool? WallpaperEnabled { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PreviousWallpaperPath { get; init; }

        public bool? AnimationsEnabled { get; init; }

        public string? AccentColor { get; init; }
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
