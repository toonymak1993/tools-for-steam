using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SteamLoader.App.Infrastructure.Popups;

/// <summary>
/// TFS Popups backend: settings, the popup overlay, theme sounds, the image
/// cache and the shell-mode volume indicator. popups-core.js decides which
/// Steam notifications come here; everything else stays Steam's.
/// </summary>
public sealed class PopupService : IDisposable
{
    private static readonly TimeSpan ImageWait = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ExplorerCheckInterval = TimeSpan.FromSeconds(3);

    private readonly PopupSettingsStore _store;
    private readonly PopupImageCache _images;
    private readonly PopupSoundPlayer _sounds = new();
    private readonly PopupOverlayHost _overlay = new();
    private readonly Func<bool> _isEnabled;
    private readonly Func<int> _scalePercent;
    private readonly HttpClient _httpClient;
    private readonly object _volumeGate = new();
    private SystemVolumeWatcher? _volumeWatcher;
    private DateTime _explorerCheckedAt = DateTime.MinValue;
    private bool _explorerRunning = true;
    private float _lastVolume = -1;
    private bool _lastMuted;
    private bool _disposed;

    public PopupService(string dataDirectory, Func<bool> isEnabled, Func<int> scalePercent)
    {
        _store = new PopupSettingsStore(Path.Combine(dataDirectory, "popups.json"));
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ToolsForSteam-Popups/1.0");
        _images = new PopupImageCache(Path.Combine(dataDirectory, "popups-cache"), _httpClient);
        _isEnabled = isEnabled;
        _scalePercent = scalePercent;
    }

    public PopupStateSnapshot GetState() => new(
        Ready: true,
        ScalePercent: ScalePercent(),
        Settings: _store.Load(),
        Themes: PopupThemeIds.All,
        Positions: PopupPositions.All);

    public PopupStateSnapshot SaveSettings(PopupSettingsUpdate update)
    {
        _store.Update(update);
        return GetState();
    }

    public async Task<PopupShowResult> ShowAsync(PopupRequest request, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return new PopupShowResult(false, "stopped");
        }

        var settings = _store.Load();
        var category = PopupCategories.IsKnown(request.Category) ? request.Category! : PopupCategories.System;
        var categorySettings = settings.Categories[category];
        if (!request.Preview && !categorySettings.Managed)
        {
            return new PopupShowResult(false, "steam");
        }

        if (!request.Preview && settings.SteamInExclusiveFullscreen && IsExclusiveFullscreenRunning())
        {
            // Our windows cannot cover an exclusive fullscreen game; Steam's overlay can.
            return new PopupShowResult(false, "fullscreen");
        }

        using var imageTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        imageTimeout.CancelAfter(ImageWait);
        var image = string.IsNullOrWhiteSpace(request.ImageUrl) && string.IsNullOrWhiteSpace(request.ImageData)
            ? null
            : await _images.GetAsync(request.ImageUrl, request.ImageData, imageTimeout.Token);

        var view = new PopupView(
            category,
            Clean(request.Title, 120) is { Length: > 0 } title ? title : "Steam",
            Clean(request.Body, 240),
            Clean(request.Detail, 120),
            image,
            request.RoundImage,
            request.Progress is { } progress && double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : null);

        var theme = request.Theme is null ? settings.Theme : PopupThemeIds.Normalize(request.Theme);
        var position = PopupPositions.Normalize(request.Position, categorySettings.Position);
        var duration = request.DurationMs is { } milliseconds
            ? TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 1500, 20000))
            : TimeSpan.FromSeconds(categorySettings.DurationSeconds);

        _overlay.Show(theme, view, position, ScalePercent() / 100d, duration);
        if (request.Sound && settings.SoundMode == PopupSoundModes.Theme)
        {
            _sounds.Play(theme, category, settings.SoundVolumePercent);
        }

        return new PopupShowResult(true);
    }

    /// <summary>Sample volume indicator for the settings page.</summary>
    public void ShowVolumePreview()
    {
        var settings = _store.Load();
        _overlay.ShowVolume(settings.Theme, _lastVolume >= 0 ? _lastVolume : 0.6f, _lastMuted, settings.VolumePosition, ScalePercent() / 100d);
    }

    /// <summary>Starts the volume watcher (cheap: Windows calls back only on changes).</summary>
    public void Start()
    {
        lock (_volumeGate)
        {
            if (_disposed || _volumeWatcher is not null)
            {
                return;
            }

            _volumeWatcher = new SystemVolumeWatcher(OnVolumeChanged);
            if (!_volumeWatcher.Start())
            {
                _volumeWatcher.Dispose();
                _volumeWatcher = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_volumeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _volumeWatcher?.Dispose();
            _volumeWatcher = null;
        }

        _overlay.Dispose();
        _httpClient.Dispose();
    }

    private void OnVolumeChanged(float level, bool muted)
    {
        // Ignore repeats (some drivers send several identical notifications).
        if (Math.Abs(level - _lastVolume) < 0.004f && muted == _lastMuted)
        {
            return;
        }

        var first = _lastVolume < 0;
        _lastVolume = level;
        _lastMuted = muted;
        if (first || _disposed || !_isEnabled())
        {
            return;
        }

        var settings = _store.Load();
        if (!settings.VolumeOverlay || IsExplorerRunning())
        {
            return;
        }

        _overlay.ShowVolume(settings.Theme, level, muted, settings.VolumePosition, ScalePercent() / 100d);
    }

    /// <summary>Windows draws its own volume flyout while Explorer runs.</summary>
    private bool IsExplorerRunning()
    {
        var now = DateTime.UtcNow;
        if (now - _explorerCheckedAt < ExplorerCheckInterval)
        {
            return _explorerRunning;
        }

        _explorerCheckedAt = now;
        try
        {
            var processes = Process.GetProcessesByName("explorer");
            _explorerRunning = processes.Length > 0;
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
        catch
        {
            _explorerRunning = true;
        }

        return _explorerRunning;
    }

    private int ScalePercent()
    {
        try
        {
            return Math.Clamp(_scalePercent(), 100, 250);
        }
        catch
        {
            return 100;
        }
    }

    internal static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = new string(value.Where(character => !char.IsControl(character) || character == ' ').ToArray()).Trim();
        return text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + "…";
    }

    private static bool IsExclusiveFullscreenRunning()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 && state == QunsRunningD3DFullScreen;
        }
        catch
        {
            return false;
        }
    }

    private const int QunsRunningD3DFullScreen = 3;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
