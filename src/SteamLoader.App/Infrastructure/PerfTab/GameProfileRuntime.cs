using System.Text.Json;
using SteamLoader.App.Infrastructure.Handheld;

namespace SteamLoader.App.Infrastructure.PerfTab;

/// <summary>
/// The setters a game profile can drive besides the TDP. Separate from the real services
/// so the switching logic can be tested without touching the display or RTSS.
/// </summary>
public interface IGameProfileTargets
{
    int GetFrameLimit();
    void SetFrameLimit(int value);
    int GetRefreshRate();
    void SetRefreshRate(int hertz);
    int GetRtssLevel();
    void SetRtssLevel(int level);
    string? GetPowerMode();
    void SetPowerMode(string mode);
    bool? GetCpuBoost();
    void SetCpuBoost(bool enabled);
}

/// <summary>
/// Applies a game profile's extra settings (frame limit, refresh rate, RTSS view, power
/// mode, CPU boost) while it is in effect and puts the user's own values back afterwards.
/// The values it replaced are kept in a small file, so a crash mid-game still restores them
/// at the next start.
/// </summary>
public sealed class GameProfileRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _sync = new();
    private readonly IGameProfileTargets _targets;
    private readonly string _baselinePath;
    private readonly Action<string> _log;
    private GameProfileExtras _baseline;

    public GameProfileRuntime(IGameProfileTargets targets, string dataDirectory, Action<string>? log = null)
    {
        _targets = targets;
        _baselinePath = Path.Combine(dataDirectory, "game-profile-baseline.json");
        _log = log ?? (_ => { });
        _baseline = LoadBaseline();
    }

    /// <summary>Values TFS replaced and will restore (null fields = nothing to restore).</summary>
    public GameProfileExtras Baseline
    {
        get
        {
            lock (_sync)
            {
                return _baseline;
            }
        }
    }

    /// <summary>After a crash: puts back whatever an interrupted profile had replaced.</summary>
    public void RestoreLeftovers()
    {
        lock (_sync)
        {
            if (!_baseline.IsEmpty)
            {
                _log("game-profile restore leftovers after restart");
                Apply(null);
            }
        }
    }

    /// <summary>Switches to the profile's extras (null = back to the user's own values).</summary>
    public void Apply(GameProfileExtras? target)
    {
        lock (_sync)
        {
            var baseline = _baseline;

            // Fields the new profile does not set: back to the user's value.
            if (target?.FrameLimit is null && baseline.FrameLimit is { } frame)
            {
                Try(() => _targets.SetFrameLimit(frame), "frame-limit");
                baseline = baseline with { FrameLimit = null };
            }

            if (target?.RefreshRate is null && baseline.RefreshRate is { } hertz)
            {
                Try(() => _targets.SetRefreshRate(hertz), "refresh-rate");
                baseline = baseline with { RefreshRate = null };
            }

            if (target?.RtssLevel is null && baseline.RtssLevel is { } level)
            {
                Try(() => _targets.SetRtssLevel(level), "rtss");
                baseline = baseline with { RtssLevel = null };
            }

            if (target?.PowerMode is null && baseline.PowerMode is { } mode)
            {
                Try(() => _targets.SetPowerMode(mode), "power-mode");
                baseline = baseline with { PowerMode = null };
            }

            if (target?.CpuBoost is null && baseline.CpuBoost is { } boost)
            {
                Try(() => _targets.SetCpuBoost(boost), "cpu-boost");
                baseline = baseline with { CpuBoost = null };
            }

            // Fields the profile sets: remember the user's value once, then apply.
            if (target is not null)
            {
                if (target.FrameLimit is { } frameTarget)
                {
                    baseline = baseline with { FrameLimit = baseline.FrameLimit ?? _targets.GetFrameLimit() };
                    Try(() => _targets.SetFrameLimit(frameTarget), "frame-limit");
                }

                if (target.RefreshRate is { } hertzTarget)
                {
                    baseline = baseline with { RefreshRate = baseline.RefreshRate ?? NullIfZero(_targets.GetRefreshRate()) };
                    Try(() => _targets.SetRefreshRate(hertzTarget), "refresh-rate");
                }

                if (target.RtssLevel is { } levelTarget)
                {
                    baseline = baseline with { RtssLevel = baseline.RtssLevel ?? _targets.GetRtssLevel() };
                    Try(() => _targets.SetRtssLevel(levelTarget), "rtss");
                }

                if (target.PowerMode is { } modeTarget)
                {
                    baseline = baseline with { PowerMode = baseline.PowerMode ?? _targets.GetPowerMode() };
                    Try(() => _targets.SetPowerMode(modeTarget), "power-mode");
                }

                if (target.CpuBoost is { } boostTarget)
                {
                    baseline = baseline with { CpuBoost = baseline.CpuBoost ?? _targets.GetCpuBoost() };
                    Try(() => _targets.SetCpuBoost(boostTarget), "cpu-boost");
                }
            }

            _baseline = baseline;
            SaveBaseline();
        }
    }

    /// <summary>
    /// A value changed in the Performance tab while the game's profile is in effect: keep the
    /// user's own value for later (once), then the caller applies and stores the new one.
    /// </summary>
    public void RememberBeforeProfileChange(string field)
    {
        lock (_sync)
        {
            _baseline = field switch
            {
                "frame-limit" => _baseline with { FrameLimit = _baseline.FrameLimit ?? _targets.GetFrameLimit() },
                "refresh-rate" => _baseline with { RefreshRate = _baseline.RefreshRate ?? NullIfZero(_targets.GetRefreshRate()) },
                "rtss" => _baseline with { RtssLevel = _baseline.RtssLevel ?? _targets.GetRtssLevel() },
                "power-mode" => _baseline with { PowerMode = _baseline.PowerMode ?? _targets.GetPowerMode() },
                "cpu-boost" => _baseline with { CpuBoost = _baseline.CpuBoost ?? _targets.GetCpuBoost() },
                _ => _baseline,
            };
            SaveBaseline();
        }
    }

    private static int? NullIfZero(int value) => value > 0 ? value : null;

    private void Try(Action action, string field)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _log($"game-profile apply-failed field={field} type={exception.GetType().Name} message={exception.Message}");
        }
    }

    private GameProfileExtras LoadBaseline()
    {
        try
        {
            return File.Exists(_baselinePath)
                ? JsonSerializer.Deserialize<GameProfileExtras>(File.ReadAllText(_baselinePath), JsonOptions) ?? new GameProfileExtras()
                : new GameProfileExtras();
        }
        catch
        {
            return new GameProfileExtras();
        }
    }

    private void SaveBaseline()
    {
        try
        {
            if (_baseline.IsEmpty)
            {
                if (File.Exists(_baselinePath))
                {
                    File.Delete(_baselinePath);
                }

                return;
            }

            HandheldPerformanceService.WriteJsonAtomically(_baselinePath, _baseline);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log($"game-profile baseline-save-failed message={exception.Message}");
        }
    }
}
