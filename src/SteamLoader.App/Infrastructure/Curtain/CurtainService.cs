using SteamLoader.App.Infrastructure.Steam;

namespace SteamLoader.App.Infrastructure.Curtain;

public sealed class CurtainService : IAsyncDisposable
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];
    private static readonly TimeSpan MaximumCurtainDuration = TimeSpan.FromSeconds(45);

    private readonly CurtainSettingsStore _settingsStore;
    private readonly SteamInstallationService _steamInstallationService;
    private readonly Func<bool> _isPluginEnabled;
    private readonly object _gate = new();
    private CurtainOverlayHost? _overlay;
    private CancellationTokenSource? _monitorCancellation;
    private int _sessionId;
    private uint _activeAppId;
    private string _activeTitle = string.Empty;

    public CurtainService(
        CurtainSettingsStore settingsStore,
        SteamInstallationService steamInstallationService,
        Func<bool> isPluginEnabled)
    {
        _settingsStore = settingsStore;
        _steamInstallationService = steamInstallationService;
        _isPluginEnabled = isPluginEnabled;
    }

    public CurtainSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            return BuildSnapshot(configuration);
        }
    }

    public CurtainSnapshot SetGameEnabled(uint appId, bool enabled)
    {
        if (appId == 0)
        {
            throw new InvalidOperationException("A Steam app id is required.");
        }

        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            configuration.DisabledAppIds.RemoveAll(candidate => candidate == appId);
            if (!enabled)
            {
                configuration.DisabledAppIds.Add(appId);
            }
            _settingsStore.Save(configuration);
            return BuildSnapshot(configuration);
        }
    }

    public async Task<CurtainLaunchResult> ShowAsync(
        uint appId,
        string? title,
        CancellationToken cancellationToken)
    {
        if (appId == 0)
        {
            return new CurtainLaunchResult(false, "A Steam app id is required.", GetSnapshot());
        }

        CurtainOverlayHost? previousOverlay;
        CancellationTokenSource monitorCancellation;
        int sessionId;
        string normalizedTitle;
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            if (!_isPluginEnabled())
            {
                return new CurtainLaunchResult(false, "TFS Curtain is disabled.", BuildSnapshot(configuration));
            }
            if (configuration.DisabledAppIds.Contains(appId))
            {
                return new CurtainLaunchResult(false, "TFS Curtain is disabled for this game.", BuildSnapshot(configuration));
            }

            previousOverlay = _overlay;
            _monitorCancellation?.Cancel();
            _monitorCancellation?.Dispose();
            monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _monitorCancellation = monitorCancellation;
            sessionId = ++_sessionId;
            normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Starting game" : title.Trim();
            _activeAppId = appId;
            _activeTitle = normalizedTitle;
            _overlay = null;
        }

        if (previousOverlay is not null)
        {
            await previousOverlay.CloseAsync(animate: false);
        }

        var foregroundAtLaunch = CurtainForegroundDetector.CaptureForegroundWindow();
        var artwork = ResolveArtwork(appId);
        var overlay = await CurtainOverlayHost.ShowAsync(
            normalizedTitle,
            artwork.BackgroundPath,
            artwork.LogoPath);

        lock (_gate)
        {
            if (sessionId != _sessionId || monitorCancellation.IsCancellationRequested)
            {
                _ = overlay.CloseAsync(animate: false);
                return new CurtainLaunchResult(false, "The launch was superseded.", BuildSnapshot(_settingsStore.Load()));
            }
            _overlay = overlay;
        }

        _ = MonitorGameWindowAsync(
            sessionId,
            normalizedTitle,
            foregroundAtLaunch,
            overlay,
            monitorCancellation.Token);
        return new CurtainLaunchResult(true, "TFS Curtain is covering the launch transition.", GetSnapshot());
    }

    public async Task<CurtainSnapshot> DismissAsync()
    {
        CurtainOverlayHost? overlay;
        lock (_gate)
        {
            overlay = DetachOverlayLocked();
        }

        if (overlay is not null)
        {
            await overlay.CloseAsync(animate: true);
        }
        return GetSnapshot();
    }

    public async ValueTask DisposeAsync()
    {
        CurtainOverlayHost? overlay;
        lock (_gate)
        {
            overlay = DetachOverlayLocked();
        }
        if (overlay is not null)
        {
            await overlay.CloseAsync(animate: false);
        }
    }

    private async Task MonitorGameWindowAsync(
        int sessionId,
        string expectedTitle,
        IntPtr foregroundAtLaunch,
        CurtainOverlayHost overlay,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var candidateHandle = IntPtr.Zero;
        var candidateSince = DateTimeOffset.MinValue;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - startedAt >= MaximumCurtainDuration)
                {
                    break;
                }

                var candidate = CurtainForegroundDetector.TryResolve();
                if (candidate is null || candidate.WindowHandle == foregroundAtLaunch)
                {
                    candidateHandle = IntPtr.Zero;
                    candidateSince = DateTimeOffset.MinValue;
                }
                else if (candidate.WindowHandle != candidateHandle)
                {
                    candidateHandle = candidate.WindowHandle;
                    candidateSince = now;
                }
                else
                {
                    var stableFor = now - candidateSince;
                    var elapsed = now - startedAt;
                    var matchingWindowedGame =
                        elapsed >= TimeSpan.FromSeconds(4) &&
                        stableFor >= TimeSpan.FromMilliseconds(1800) &&
                        TitlesLikelyMatch(expectedTitle, candidate.WindowTitle);
                    if ((candidate.IsFullscreen && stableFor >= TimeSpan.FromMilliseconds(800)) ||
                        matchingWindowedGame)
                    {
                        break;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(140), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        lock (_gate)
        {
            if (sessionId != _sessionId || !ReferenceEquals(_overlay, overlay))
            {
                return;
            }
            _ = DetachOverlayLocked();
        }
        await overlay.CloseAsync(animate: true);
    }

    internal static bool TitlesLikelyMatch(string expectedTitle, string windowTitle)
    {
        var ignoredWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "edition", "enhanced", "game", "launcher", "steam", "the", "windows"
        };
        var expectedWords = expectedTitle
            .Split([' ', ':', '-', '_', '–', '—'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()))
            .Where(word => word.Length >= 4 && !ignoredWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (expectedWords.Length == 0 || string.IsNullOrWhiteSpace(windowTitle))
        {
            return false;
        }

        return expectedWords.Any(word =>
            windowTitle.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private CurtainOverlayHost? DetachOverlayLocked()
    {
        _monitorCancellation?.Cancel();
        _monitorCancellation?.Dispose();
        _monitorCancellation = null;
        var overlay = _overlay;
        _overlay = null;
        _activeAppId = 0;
        _activeTitle = string.Empty;
        _sessionId++;
        return overlay;
    }

    private CurtainSnapshot BuildSnapshot(CurtainConfiguration configuration) => new(
        Enabled: _isPluginEnabled(),
        Active: _overlay is not null,
        ActiveAppId: _activeAppId,
        ActiveTitle: _activeTitle,
        DisabledAppIds: configuration.DisabledAppIds.ToArray(),
        MaximumDurationSeconds: (int)MaximumCurtainDuration.TotalSeconds);

    private CurtainArtwork ResolveArtwork(uint appId)
    {
        var steamRoot = _steamInstallationService.ResolveSteamRootPath();
        if (string.IsNullOrWhiteSpace(steamRoot))
        {
            return new CurtainArtwork(string.Empty, string.Empty);
        }

        var backgroundCandidates = new List<string>();
        var logoCandidates = new List<string>();
        var libraryCache = Path.Combine(steamRoot, "appcache", "librarycache", appId.ToString());
        AddStemCandidates(backgroundCandidates, libraryCache, "library_hero");
        AddStemCandidates(backgroundCandidates, libraryCache, "library_600x900");
        AddStemCandidates(logoCandidates, libraryCache, "logo");

        var userdata = Path.Combine(steamRoot, "userdata");
        if (Directory.Exists(userdata))
        {
            foreach (var profileDirectory in Directory.EnumerateDirectories(userdata))
            {
                var grid = Path.Combine(profileDirectory, "config", "grid");
                AddStemCandidates(backgroundCandidates, grid, $"{appId}_hero");
                AddStemCandidates(backgroundCandidates, grid, appId.ToString());
                AddStemCandidates(backgroundCandidates, grid, $"{appId}p");
                AddStemCandidates(logoCandidates, grid, $"{appId}_logo");
            }
        }

        return new CurtainArtwork(
            backgroundCandidates.FirstOrDefault(File.Exists) ?? string.Empty,
            logoCandidates.FirstOrDefault(File.Exists) ?? string.Empty);
    }

    private static void AddStemCandidates(List<string> candidates, string directory, string stem)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }
        foreach (var extension in ImageExtensions)
        {
            candidates.Add(Path.Combine(directory, $"{stem}{extension}"));
        }
    }

    private sealed record CurtainArtwork(string BackgroundPath, string LogoPath);
}

public sealed record CurtainSnapshot(
    bool Enabled,
    bool Active,
    uint ActiveAppId,
    string ActiveTitle,
    IReadOnlyList<uint> DisabledAppIds,
    int MaximumDurationSeconds);

public sealed record CurtainLaunchResult(
    bool Shown,
    string Message,
    CurtainSnapshot State);
