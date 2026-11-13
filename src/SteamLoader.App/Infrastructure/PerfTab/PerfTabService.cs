using SteamLoader.App.Infrastructure.Display;
using SteamLoader.App.Infrastructure.Handheld;
using SteamLoader.App.Infrastructure.Performance;
using SteamLoader.App.Infrastructure.SystemTools;

namespace SteamLoader.App.Infrastructure.PerfTab;

public sealed record PerfTabFrameLimit(int Value, IReadOnlyList<int> Options, bool Available);

public sealed record PerfTabRefreshRate(int Current, IReadOnlyList<int> Options, bool Available);

public sealed record PerfTabToggle(bool Supported, bool Enabled);

/// <summary>RTSS overlay level (0 = off) and the titles of all levels.</summary>
public sealed record PerfTabRtss(bool Available, int Level, IReadOnlyList<string> Levels);

/// <summary>
/// The device the TDP slider controls. LimitSource: "manufacturer", "vendor-app" or "amd".
/// MaximumBattery is 0 when the maker allows the same maximum on battery.
/// </summary>
public sealed record PerfTabDevice(
    string Name,
    string Cpu,
    string LimitSource,
    int Minimum,
    int MaximumAc,
    int MaximumBattery);

/// <summary>
/// Warning is empty, "pawnio-missing", "vendor-app" (Detail = app names) or "error" (Detail = SMU message).
/// Value and Maximum are those of the current power source.
/// </summary>
public sealed record PerfTabTdp(
    bool Supported,
    int Minimum,
    int Maximum,
    int Value,
    string Status,
    string Warning = "",
    string Detail = "",
    string PowerSource = "ac",
    int AcValue = 0,
    int BatteryValue = 0,
    HandheldTdpConfirmation? Confirmation = null);

public sealed record PerfTabGameProfile(string Key, string Title, int AcWatts, int BatteryWatts, bool Active);

/// <summary>Running game (empty title = none), whether it uses its own profile, and all saved profiles.</summary>
public sealed record PerfTabProfiles(
    bool Supported,
    bool AutoApply,
    string GameKey,
    string GameTitle,
    bool GameUsesProfile,
    IReadOnlyList<PerfTabGameProfile> Saved);

/// <summary>One game profile for its settings page; null extras = "global value".</summary>
public sealed record PerfTabProfileDetail(
    string Key,
    string Title,
    int AcWatts,
    int BatteryWatts,
    int? FrameLimit,
    int? RefreshRate,
    int? RtssLevel,
    string? PowerMode,
    bool? CpuBoost,
    int MinimumWatts,
    int MaximumAcWatts,
    int MaximumBatteryWatts,
    IReadOnlyList<int> FrameLimitOptions,
    IReadOnlyList<int> RefreshOptions,
    IReadOnlyList<string> RtssLevels,
    bool CpuBoostSupported,
    bool Active);

public sealed record PerfTabProfileUpdate(
    string Key,
    int AcWatts,
    int BatteryWatts,
    int? FrameLimit,
    int? RefreshRate,
    int? RtssLevel,
    string? PowerMode,
    bool? CpuBoost);

public sealed record PerfTabState(
    PerfTabFrameLimit FrameLimit,
    PerfTabRefreshRate RefreshRate,
    string? PowerMode,
    PerfTabToggle CpuBoost,
    PerfTabTdp Tdp,
    bool OnBattery,
    PerfTabDevice? Device = null,
    PerfTabRtss? Rtss = null,
    PerfTabProfiles? Profiles = null);

/// <summary>
/// Everything the TFS section in Steam's Performance tab shows, in one call,
/// built from the existing TFS services (RTSS overlay and frame limit, display
/// modes, Windows power mode, CPU boost, handheld TDP and game profiles).
/// </summary>
public sealed class PerfTabService
{
    internal static readonly int[] FrameLimitSteps = [0, 30, 40, 45, 60, 72, 90, 120, 144];

    private readonly TfsPerformanceService _performance;
    private readonly DisplaySwitchService _display;
    private readonly HandheldPerformanceService _handheld;
    private readonly HandheldSystemControlService _systemControls;

    private readonly GameProfileRuntime _profiles;

    public PerfTabService(
        TfsPerformanceService performance,
        DisplaySwitchService display,
        HandheldPerformanceService handheld,
        string dataDirectory)
    {
        _performance = performance;
        _display = display;
        _handheld = handheld;
        _systemControls = new HandheldSystemControlService(dataDirectory);
        _profiles = new GameProfileRuntime(new Targets(this), dataDirectory);
        _profiles.RestoreLeftovers();
        _handheld.ActiveGameProfileChanged += profile => _profiles.Apply(profile?.Extras);
    }

    private string CurrentPowerSource()
        => string.Equals(_handheld.GetSnapshot().PowerSource, "battery", StringComparison.OrdinalIgnoreCase) ? "battery" : "ac";

    /// <summary>The real setters behind a game profile.</summary>
    private sealed class Targets(PerfTabService owner) : IGameProfileTargets
    {
        public int GetFrameLimit() => owner._performance.GetSnapshot().Settings.FrameLimit;

        public void SetFrameLimit(int value) => owner._performance.SetSettingValue("frame-limit", Math.Clamp(value, 0, 500));

        public int GetRefreshRate()
        {
            var (current, _) = owner._display.GetRefreshRatesForCurrentResolution();
            return current;
        }

        public void SetRefreshRate(int hertz)
        {
            var (_, options) = owner._display.GetRefreshRatesForCurrentResolution();
            if (options.Contains(hertz))
            {
                owner._display.SetRefreshRate(hertz);
            }
        }

        public int GetRtssLevel() => owner._performance.GetSnapshot().Settings.OverlayLevel;

        public void SetRtssLevel(int level) => owner._performance.SetOverlayLevel(level);

        public string? GetPowerMode() => WindowsPowerModeService.GetMode();

        public void SetPowerMode(string mode) => WindowsPowerModeService.SetMode(mode);

        public bool? GetCpuBoost()
        {
            var boost = owner._systemControls.GetCpuBoost();
            if (!boost.Supported)
            {
                return null;
            }

            return owner.CurrentPowerSource() == "battery" ? boost.BatteryMode != 0 : boost.AcMode != 0;
        }

        public void SetCpuBoost(bool enabled) => owner._systemControls.SetCpuBoost(owner.CurrentPowerSource(), enabled);
    }

    /// <summary>
    /// While the running game uses its own profile, a change in the Performance tab goes into
    /// that profile (Steam Deck behaviour) and the user's own value comes back after the game.
    /// </summary>
    private void ApplySetting(string field, Action apply, Func<GameProfileExtras, GameProfileExtras> store)
    {
        if (_handheld.GetActiveProfile() is null)
        {
            apply();
            return;
        }

        _profiles.RememberBeforeProfileChange(field);
        apply();
        _handheld.TryUpdateActiveProfileExtras(store);
    }

    public PerfTabState GetState()
    {
        var performance = _performance.GetSnapshot();
        var frameLimit = new PerfTabFrameLimit(
            performance.Settings.FrameLimit,
            FrameLimitSteps,
            performance.Installation.Installed);
        var rtss = new PerfTabRtss(
            performance.Installation.Installed,
            performance.Settings.OverlayLevel,
            performance.Settings.OverlayLevels.OrderBy(level => level.Value).Select(level => level.Title).ToArray());

        PerfTabRefreshRate refresh;
        try
        {
            var (current, options) = _display.GetRefreshRatesForCurrentResolution();
            refresh = new PerfTabRefreshRate(current, options, options.Count > 1);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            refresh = new PerfTabRefreshRate(0, [], false);
        }

        var handheld = _handheld.GetSnapshot();
        var onBattery = string.Equals(handheld.PowerSource, "battery", StringComparison.OrdinalIgnoreCase);
        var boost = _systemControls.GetCpuBoost();
        var tdpSupported = handheld.Supported && handheld.MaximumTdpWatts > 0;
        var vendorApps = tdpSupported ? HandheldVendorAppDetector.FindRunning() : [];
        var (warning, detail) = !tdpSupported
            ? ("", "")
            : !handheld.PawnIoInstalled
                ? ("pawnio-missing", "")
                : vendorApps.Count > 0
                    ? ("vendor-app", string.Join(", ", vendorApps))
                    : !string.IsNullOrWhiteSpace(handheld.ErrorText)
                        ? ("error", handheld.ErrorText)
                        : ("", "");
        var tdp = new PerfTabTdp(
            tdpSupported,
            handheld.MinimumTdpWatts,
            handheld.MaximumTdpWatts,
            handheld.SelectedTdpWatts,
            handheld.StatusText,
            warning,
            detail,
            onBattery ? "battery" : "ac",
            handheld.ActiveProfile is null ? handheld.GlobalAcTdpWatts : HandheldPerformanceService.ResolveProfileTdp(handheld.ActiveProfile, "ac"),
            handheld.ActiveProfile is null ? handheld.GlobalBatteryTdpWatts : HandheldPerformanceService.ResolveProfileTdp(handheld.ActiveProfile, "battery"),
            tdpSupported ? _handheld.GetTdpConfirmation() : null);

        var device = HandheldDeviceCatalog.Detect();
        var deviceInfo = tdpSupported && device.Tdp is not null
            ? new PerfTabDevice(
                device.DisplayName,
                device.Tdp.CpuName,
                device.Tdp.LimitSource,
                device.Tdp.MinimumWatts,
                device.Tdp.MaximumWatts,
                device.Tdp.MaximumFor("battery") < device.Tdp.MaximumWatts ? device.Tdp.MaximumFor("battery") : 0)
            : null;

        var profiles = new PerfTabProfiles(
            tdpSupported,
            handheld.AutoProfilesEnabled,
            handheld.CurrentGame?.Key ?? string.Empty,
            handheld.CurrentGame?.Title ?? string.Empty,
            handheld.ActiveProfile is not null,
            handheld.Profiles
                .Select(profile => new PerfTabGameProfile(
                    profile.Key,
                    profile.Title,
                    HandheldPerformanceService.ResolveProfileTdp(profile, "ac"),
                    HandheldPerformanceService.ResolveProfileTdp(profile, "battery"),
                    handheld.ActiveProfile is not null &&
                        string.Equals(handheld.ActiveProfile.Key, profile.Key, StringComparison.OrdinalIgnoreCase)))
                .ToArray());

        return new PerfTabState(
            frameLimit,
            refresh,
            WindowsPowerModeService.GetMode(),
            new PerfTabToggle(boost.Supported, onBattery ? boost.BatteryMode != 0 : boost.AcMode != 0),
            tdp,
            onBattery,
            deviceInfo,
            rtss,
            profiles);
    }

    public PerfTabState SetFrameLimit(int value)
    {
        var clamped = Math.Clamp(value, 0, 500);
        ApplySetting("frame-limit",
            () => _performance.SetSettingValue("frame-limit", clamped),
            extras => extras with { FrameLimit = clamped });
        return GetState();
    }

    public PerfTabState SetRtssLevel(int level)
    {
        ApplySetting("rtss",
            () => _performance.SetOverlayLevel(level),
            extras => extras with { RtssLevel = level });
        return GetState();
    }

    public PerfTabState SetRefreshRate(int hertz)
    {
        ApplySetting("refresh-rate",
            () => _display.SetRefreshRate(hertz),
            extras => extras with { RefreshRate = hertz });
        return GetState();
    }

    public PerfTabState SetPowerMode(string mode)
    {
        ApplySetting("power-mode",
            () => WindowsPowerModeService.SetMode(mode),
            extras => extras with { PowerMode = mode });
        return GetState();
    }

    public PerfTabState SetCpuBoost(bool enabled)
    {
        var source = CurrentPowerSource();
        ApplySetting("cpu-boost",
            () => _systemControls.SetCpuBoost(source, enabled),
            extras => extras with { CpuBoost = enabled });
        return GetState();
    }

    /// <summary>Everything the profile page shows for one game profile.</summary>
    public PerfTabProfileDetail GetProfile(string key)
    {
        var profile = _handheld.GetProfile(key) ?? throw new InvalidOperationException("The selected game profile no longer exists.");
        var device = HandheldDeviceCatalog.Detect();
        var performance = _performance.GetSnapshot();
        IReadOnlyList<int> refreshOptions;
        try
        {
            var (_, options) = _display.GetRefreshRatesForCurrentResolution();
            refreshOptions = options;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            refreshOptions = [];
        }

        var extras = profile.Extras ?? new GameProfileExtras();
        var active = _handheld.GetActiveProfile();
        return new PerfTabProfileDetail(
            profile.Key,
            profile.Title,
            HandheldPerformanceService.ResolveProfileTdp(profile, "ac"),
            HandheldPerformanceService.ResolveProfileTdp(profile, "battery"),
            extras.FrameLimit,
            extras.RefreshRate,
            extras.RtssLevel,
            extras.PowerMode,
            extras.CpuBoost,
            device.MinimumTdpWatts,
            device.Tdp?.MaximumFor("ac") ?? device.MaximumTdpWatts,
            device.Tdp?.MaximumFor("battery") ?? device.MaximumTdpWatts,
            FrameLimitSteps,
            refreshOptions,
            performance.Settings.OverlayLevels.OrderBy(level => level.Value).Select(level => level.Title).ToArray(),
            _systemControls.GetCpuBoost().Supported,
            active is not null && string.Equals(active.Key, profile.Key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Running game first, then every open app window, each once.</summary>
    public IReadOnlyList<PerfTabRunningApp> ListRunningApps()
    {
        var game = _handheld.GetSnapshot().CurrentGame;
        var apps = new List<PerfTabRunningApp>();
        if (game is not null)
        {
            apps.Add(new PerfTabRunningApp(game.Key, game.Title, game.ExecutablePath, game.AppId, true, _handheld.HasProfile(game.Key)));
        }

        foreach (var window in RunningAppCatalog.ListWindows())
        {
            if (game is not null &&
                (string.Equals(window.Key, game.Key, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(window.ExecutablePath, game.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            apps.Add(new PerfTabRunningApp(window.Key, window.Title, window.ExecutablePath, string.Empty, false, _handheld.HasProfile(window.Key)));
        }

        return apps;
    }

    /// <summary>Creates a profile for a game or app picked from <see cref="ListRunningApps"/>.</summary>
    public PerfTabProfileDetail AddProfile(string key)
    {
        var app = ListRunningApps().FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("This game or app is no longer running.");
        _handheld.CreateProfile(app.Key, app.AppId, app.Title, app.ExecutablePath);
        return GetProfile(app.Key);
    }

    /// <summary>
    /// Key the game monitor uses for a running Steam app. Steam logs non-Steam shortcuts
    /// (app id 0x80000000 and up) by their 64-bit game id: (appId &lt;&lt; 32) | 0x02000000.
    /// </summary>
    internal static string SteamProfileKey(uint appId)
        => appId >= 0x80000000
            ? $"steam:{((ulong)appId << 32) | 0x02000000UL}"
            : $"steam:{appId}";

    /// <summary>
    /// Context menu "TFS Game Profile": a profile for a library game before it runs, keyed
    /// like the running Steam game ("steam:&lt;appId&gt;").
    /// </summary>
    public PerfTabProfileDetail AddProfileForApp(uint appId, string? title)
    {
        if (appId == 0)
        {
            throw new ArgumentException("No game was selected.");
        }

        var key = SteamProfileKey(appId);
        _handheld.CreateProfile(key, key["steam:".Length..],
            string.IsNullOrWhiteSpace(title) ? $"App {appId}" : title.Trim(), string.Empty);
        return GetProfile(key);
    }

    public PerfTabProfileDetail SaveProfile(PerfTabProfileUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.Key))
        {
            throw new ArgumentException("No profile was selected.");
        }

        _handheld.UpdateProfile(
            update.Key,
            update.AcWatts,
            update.BatteryWatts,
            new GameProfileExtras(update.FrameLimit, update.RefreshRate, update.RtssLevel, update.PowerMode, update.CpuBoost));
        return GetProfile(update.Key);
    }

    public PerfTabState SetTdp(int watts)
    {
        _handheld.SetTdp(watts);
        return GetState();
    }

    public PerfTabState SetGameProfile(bool enabled)
    {
        _handheld.SetCurrentGameProfileEnabled(enabled);
        return GetState();
    }

    public PerfTabState SetAutoProfiles(bool enabled)
    {
        _handheld.SetAutoProfilesEnabled(enabled);
        return GetState();
    }

    public PerfTabState DeleteProfile(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("No profile was selected.");
        }

        _handheld.DeleteProfile(key);
        return GetState();
    }
}
