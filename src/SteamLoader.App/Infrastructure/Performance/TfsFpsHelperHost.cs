using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;

namespace SteamLoader.App.Infrastructure.Performance;

public sealed class TfsFpsHelperHost
{
    private readonly PerformanceSettingsStore _settingsStore;
    private readonly PerformanceStatusStore _statusStore;
    private readonly EtwFpsSampler _sampler = new();
    private readonly DispatcherTimer _managementTimer;
    private readonly DispatcherTimer _overlayTimer;

    private PerformanceSettingsConfiguration _configuration = new();
    private ForegroundTargetCandidate? _currentTarget;
    private PerformanceRuntimeStatus _lastStatus = new();
    private SteamOsPerformanceOverlayWindow? _window;
    private System.Windows.Application? _application;
    private DateTimeOffset _lastTelemetrySampleAt = DateTimeOffset.MinValue;
    private TimeSpan _lastProcessCpuTime = TimeSpan.Zero;
    private double _targetCpuPercent;
    private long _targetMemoryMb;
    private OverlayMetricsSnapshot _lastMetrics = OverlayMetricsSnapshot.Empty;

    public TfsFpsHelperHost(PerformanceSettingsStore settingsStore, PerformanceStatusStore statusStore)
    {
        _settingsStore = settingsStore;
        _statusStore = statusStore;
        _managementTimer = new DispatcherTimer(DispatcherPriority.Background);
        _managementTimer.Tick += (_, _) => OnManagementTick();
        _overlayTimer = new DispatcherTimer(DispatcherPriority.Render);
        _overlayTimer.Tick += (_, _) => OnOverlayTick();
    }

    public int Run()
    {
        using var mutex = new Mutex(true, SteamLoaderRuntime.FpsHelperMutexName, out var createdNew);
        if (!createdNew)
        {
            return 0;
        }

        _configuration = _settingsStore.Load();
        if (!_configuration.OverlayEnabled)
        {
            _statusStore.Save(new PerformanceRuntimeStatus
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                DetailText = "TFS FPS Overlay is idle."
            });
            return 0;
        }

        _statusStore.Save(new PerformanceRuntimeStatus
        {
            Elevated = EtwFpsSampler.IsCurrentProcessElevated(),
            HelperProcessId = Environment.ProcessId,
            DetailText = "Starting the elevated TFS FPS helper...",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });

        _application = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        _window = new SteamOsPerformanceOverlayWindow();
        _application.Exit += (_, _) => FlushStoppedStatus("TFS FPS Overlay stopped.");
        _application.Startup += (_, _) =>
        {
            ApplyConfigurationToWindow();
            UpdateTimerIntervals();
            TryStartSampler();
            _window!.Show();
            OnManagementTick();
            OnOverlayTick();
            _managementTimer.Start();
            _overlayTimer.Start();
        };

        return _application.Run(_window);
    }

    private void OnManagementTick()
    {
        _configuration = _settingsStore.Load();
        if (!_configuration.OverlayEnabled)
        {
            StopHelper("TFS FPS Overlay stopped.");
            return;
        }

        ApplyConfigurationToWindow();
        UpdateTimerIntervals();

        if (!_sampler.IsRunning && string.IsNullOrWhiteSpace(_sampler.LastError))
        {
            TryStartSampler();
        }

        _sampler.ApplyConfiguration(_configuration);

        var target = ResolveTarget();
        if (!SameTarget(_currentTarget, target))
        {
            _currentTarget = target;
            _sampler.SetTarget(target);
            ResetProcessTelemetry();
        }

        SampleTargetTelemetryIfNeeded();

        var metrics = _sampler.GetSnapshot();
        _lastMetrics = metrics;
        _lastStatus = new PerformanceRuntimeStatus
        {
            OverlayVisible = true,
            Elevated = EtwFpsSampler.IsCurrentProcessElevated(),
            HelperProcessId = Environment.ProcessId,
            TargetProcessId = _currentTarget?.ProcessId ?? 0,
            TargetProcessName = _currentTarget?.ProcessName ?? string.Empty,
            TargetWindowTitle = _currentTarget?.WindowTitle ?? string.Empty,
            FramesPerSecond = metrics.FramesPerSecond,
            FrameTimeMs = metrics.FrameTimeMs,
            OnePercentLowFps = metrics.OnePercentLowFps,
            FramePacingMs = metrics.FramePacingMs,
            TargetCpuPercent = _targetCpuPercent,
            TargetMemoryMb = _targetMemoryMb,
            DetailText = BuildDetailText(metrics),
            ErrorText = _sampler.LastError,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        _statusStore.Save(_lastStatus);
    }

    private void OnOverlayTick()
    {
        _window?.Render(_configuration, _lastStatus, _lastMetrics);
    }

    private void ApplyConfigurationToWindow()
    {
        _window?.ApplyConfiguration(_configuration);
    }

    private void UpdateTimerIntervals()
    {
        var metricInterval = TimeSpan.FromMilliseconds(Math.Max(80d, 1000d / Math.Max(1d, _configuration.MetricPollRate)));
        var drawInterval = TimeSpan.FromMilliseconds(Math.Max(50d, 1000d / Math.Max(1d, _configuration.OverlayDrawRate)));

        if (_managementTimer.Interval != metricInterval)
        {
            _managementTimer.Interval = metricInterval;
        }

        if (_overlayTimer.Interval != drawInterval)
        {
            _overlayTimer.Interval = drawInterval;
        }
    }

    private void TryStartSampler()
    {
        try
        {
            _sampler.Start(_configuration);
        }
        catch (Exception exception)
        {
            _sampler.SetError(exception.Message);
        }
    }

    private ForegroundTargetCandidate? ResolveTarget()
    {
        if (_configuration.AutoTargetEnabled)
        {
            var foreground = PerformanceForegroundTargetResolver.TryResolve();
            if (foreground is not null)
            {
                return foreground;
            }

            return _currentTarget is not null && IsProcessAlive(_currentTarget.ProcessId)
                ? _currentTarget
                : null;
        }

        if (_currentTarget is not null && IsProcessAlive(_currentTarget.ProcessId))
        {
            return _currentTarget;
        }

        return PerformanceForegroundTargetResolver.TryResolve();
    }

    private void SampleTargetTelemetryIfNeeded()
    {
        if (_currentTarget is null)
        {
            ResetProcessTelemetry();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_lastTelemetrySampleAt != DateTimeOffset.MinValue &&
            now - _lastTelemetrySampleAt < TimeSpan.FromMilliseconds(_configuration.TelemetrySamplingPeriodMs))
        {
            return;
        }

        _lastTelemetrySampleAt = now;

        try
        {
            using var process = Process.GetProcessById(_currentTarget.ProcessId);
            process.Refresh();

            var currentCpuTime = process.TotalProcessorTime;
            if (_lastProcessCpuTime != TimeSpan.Zero)
            {
                var wallClockMs = Math.Max(1d, _configuration.TelemetrySamplingPeriodMs);
                var cpuDeltaMs = Math.Max(0d, (currentCpuTime - _lastProcessCpuTime).TotalMilliseconds);
                _targetCpuPercent = Math.Clamp(cpuDeltaMs / (wallClockMs * Environment.ProcessorCount) * 100d, 0d, 100d);
            }

            _lastProcessCpuTime = currentCpuTime;
            _targetMemoryMb = Math.Max(0L, process.WorkingSet64 / (1024L * 1024L));
        }
        catch
        {
            _targetCpuPercent = 0d;
            _targetMemoryMb = 0L;
        }
    }

    private void ResetProcessTelemetry()
    {
        _lastTelemetrySampleAt = DateTimeOffset.MinValue;
        _lastProcessCpuTime = TimeSpan.Zero;
        _targetCpuPercent = 0d;
        _targetMemoryMb = 0L;
    }

    private string BuildDetailText(OverlayMetricsSnapshot metrics)
    {
        if (!string.IsNullOrWhiteSpace(_sampler.LastError))
        {
            return _sampler.LastError;
        }

        if (_currentTarget is null)
        {
            return _configuration.AutoTargetEnabled
                ? "Waiting for the active game window."
                : "Waiting for a target process.";
        }

        if (metrics.SampleCount == 0)
        {
            return $"Attached to {_currentTarget.ProcessName}. Waiting for frame events.";
        }

        return $"{metrics.FrameTimeMs:0.0} ms frametime - 1% low {metrics.OnePercentLowFps:0.#} FPS - CPU {_targetCpuPercent:0.#}% - RAM {_targetMemoryMb} MB";
    }

    private void StopHelper(string detailText)
    {
        _managementTimer.Stop();
        _overlayTimer.Stop();
        _sampler.Stop();
        FlushStoppedStatus(detailText);

        if (_application is not null && !_application.Dispatcher.HasShutdownStarted)
        {
            _application.Dispatcher.BeginInvoke(() =>
            {
                _window?.Close();
                _application.Shutdown();
            });
        }
    }

    private void FlushStoppedStatus(string detailText)
    {
        _statusStore.Save(new PerformanceRuntimeStatus
        {
            Elevated = EtwFpsSampler.IsCurrentProcessElevated(),
            DetailText = detailText,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
    }

    private static bool SameTarget(ForegroundTargetCandidate? left, ForegroundTargetCandidate? right)
    {
        return left?.ProcessId == right?.ProcessId &&
               string.Equals(left?.WindowTitle, right?.WindowTitle, StringComparison.Ordinal);
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class EtwFpsSampler
{
    private static readonly Guid DxgiProviderGuid = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3d9ProviderGuid = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const ulong PresentKeywords = 0x8000000000000002;

    private readonly object _gate = new();
    private readonly Queue<double> _frameTimesMs = new();
    private readonly Queue<(double TimestampMs, double DeltaMs)> _frameIntervals = new();

    private CancellationTokenSource? _cts;
    private Task? _processingTask;
    private TraceEventSession? _session;
    private PerformanceSettingsConfiguration _configuration = new();
    private int _targetProcessId;
    private double? _lastFrameTimestampMs;

    public bool IsRunning { get; private set; }

    public string LastError { get; private set; } = string.Empty;

    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public void Start(PerformanceSettingsConfiguration configuration)
    {
        lock (_gate)
        {
            if (IsRunning)
            {
                _configuration = configuration;
                return;
            }

            if (!IsCurrentProcessElevated())
            {
                throw new InvalidOperationException("The built-in TFS FPS meter needs Windows admin rights for ETW frame capture.");
            }

            _configuration = configuration;
            _cts = new CancellationTokenSource();
            _processingTask = Task.Factory.StartNew(
                () => RunTraceLoop(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            IsRunning = true;
            LastError = string.Empty;
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? task;
        TraceEventSession? session;

        lock (_gate)
        {
            cts = _cts;
            task = _processingTask;
            session = _session;
            _cts = null;
            _processingTask = null;
            _session = null;
            IsRunning = false;
        }

        try
        {
            cts?.Cancel();
            session?.Source.StopProcessing();
            session?.Dispose();
            task?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
        finally
        {
            cts?.Dispose();
        }
    }

    public void ApplyConfiguration(PerformanceSettingsConfiguration configuration)
    {
        lock (_gate)
        {
            _configuration = configuration;
        }
    }

    public void SetTarget(ForegroundTargetCandidate? target)
    {
        lock (_gate)
        {
            _targetProcessId = target?.ProcessId ?? 0;
            _lastFrameTimestampMs = null;
            _frameTimesMs.Clear();
            _frameIntervals.Clear();
        }
    }

    public void SetError(string errorText)
    {
        lock (_gate)
        {
            LastError = errorText;
        }
    }

    public OverlayMetricsSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            TrimSamples();
            if (_frameTimesMs.Count < 2 || _frameIntervals.Count == 0)
            {
                return OverlayMetricsSnapshot.Empty;
            }

            var frameTimes = _frameTimesMs.ToArray();
            var intervals = _frameIntervals.Select(entry => entry.DeltaMs).ToArray();
            var recentIntervals = intervals
                .Skip(Math.Max(0, intervals.Length - 72))
                .ToArray();
            var fps = frameTimes.Length > 1 && frameTimes[^1] > frameTimes[0]
                ? (frameTimes.Length - 1) * 1000d / (frameTimes[^1] - frameTimes[0])
                : 0d;
            var frameTimeMs = intervals.Average();
            var sorted = intervals.OrderBy(value => value).ToArray();
            var percentileIndex = Math.Clamp((int)Math.Ceiling(sorted.Length * 0.99d) - 1, 0, Math.Max(0, sorted.Length - 1));
            var onePercentLow = sorted.Length > 0 && sorted[percentileIndex] > 0d
                ? 1000d / sorted[percentileIndex]
                : 0d;
            var variance = intervals.Select(value => Math.Pow(value - frameTimeMs, 2d)).Average();

            return new OverlayMetricsSnapshot(
                fps,
                frameTimeMs,
                onePercentLow,
                Math.Sqrt(variance),
                intervals.Length,
                recentIntervals,
                recentIntervals
                    .Select(value => value > 0d ? 1000d / value : 0d)
                    .ToArray());
        }
    }

    private void RunTraceLoop(CancellationToken cancellationToken)
    {
        try
        {
            using var session = new TraceEventSession($"ToolsForSteam-Fps-{Environment.ProcessId}-{Guid.NewGuid():N}");
            session.StopOnDispose = true;
            lock (_gate)
            {
                _session = session;
            }

            session.EnableProvider(DxgiProviderGuid, TraceEventLevel.Always, PresentKeywords);
            session.EnableProvider(D3d9ProviderGuid, TraceEventLevel.Always, PresentKeywords);
            session.Source.Dynamic.All += HandleEvent;
            session.Source.Process();
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                LastError = exception.Message;
                IsRunning = false;
            }
        }
    }

    private void HandleEvent(TraceEvent data)
    {
        lock (_gate)
        {
            if (_targetProcessId <= 0 || data.ProcessID != _targetProcessId)
            {
                return;
            }

            if (!IsPresentStartEvent(data))
            {
                return;
            }

            LastError = string.Empty;
            var timestampMs = data.TimeStampRelativeMSec;
            if (_lastFrameTimestampMs is double lastTimestampMs)
            {
                var deltaMs = timestampMs - lastTimestampMs;
                if (deltaMs > 0d && deltaMs < 1000d)
                {
                    _frameIntervals.Enqueue((timestampMs, deltaMs));
                }
            }

            _frameTimesMs.Enqueue(timestampMs);
            _lastFrameTimestampMs = timestampMs;
            TrimSamples();
        }
    }

    private static bool IsPresentStartEvent(TraceEvent data)
    {
        if (data.ProviderGuid == DxgiProviderGuid)
        {
            var eventId = (int)data.ID;
            return eventId is 0x002a or 0x0037;
        }

        if (data.ProviderGuid == D3d9ProviderGuid)
        {
            return (int)data.ID == 0x0001;
        }

        return false;
    }

    private void TrimSamples()
    {
        if (_frameTimesMs.Count == 0)
        {
            return;
        }

        var frameTimes = _frameTimesMs.ToArray();
        var latestTimestampMs = frameTimes[^1];
        var windowMs = Math.Max(100d, _configuration.MetricsWindow);
        var minimumTimestampMs = latestTimestampMs - windowMs;

        while (_frameTimesMs.Count > 0 && _frameTimesMs.Peek() < minimumTimestampMs)
        {
            _frameTimesMs.Dequeue();
        }

        while (_frameIntervals.Count > 0 && _frameIntervals.Peek().TimestampMs < minimumTimestampMs)
        {
            _frameIntervals.Dequeue();
        }
    }
}

internal sealed record OverlayMetricsSnapshot(
    double FramesPerSecond,
    double FrameTimeMs,
    double OnePercentLowFps,
    double FramePacingMs,
    int SampleCount,
    IReadOnlyList<double> RecentFrameTimesMs,
    IReadOnlyList<double> RecentFpsSamples)
{
    public static OverlayMetricsSnapshot Empty { get; } = new(
        0d,
        0d,
        0d,
        0d,
        0,
        Array.Empty<double>(),
        Array.Empty<double>());
}

internal sealed class TfsPerformanceOverlayWindow : Window
{
    private readonly Border _cardBorder;
    private readonly Border _accentBar;
    private readonly TextBlock _headerText;
    private readonly Border _modeBadge;
    private readonly TextBlock _modeBadgeText;
    private readonly TextBlock _fpsText;
    private readonly TextBlock _summaryText;
    private readonly Border _graphBorder;
    private readonly Canvas _graphCanvas;
    private readonly TextBlock _graphCaptionText;
    private readonly Polygon _graphFill;
    private readonly Polyline _graphLine;
    private readonly Line _graphMidLine;
    private readonly WrapPanel _metricsPanel;
    private readonly OverlayMetricChip[] _metricChips;
    private readonly TextBlock _detailText;

    public TfsPerformanceOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = MediaBrushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        SnapsToDevicePixels = true;

        _headerText = new TextBlock
        {
            Foreground = MediaBrushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        _modeBadgeText = new TextBlock
        {
            Foreground = MediaBrushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _modeBadge = new Border
        {
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(999),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Child = _modeBadgeText
        };
        _fpsText = new TextBlock
        {
            Foreground = MediaBrushes.White,
            FontSize = 38,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 6, 0, 0)
        };
        _summaryText = new TextBlock
        {
            Foreground = new SolidColorBrush(MediaColor.FromRgb(200, 218, 236)),
            FontSize = 14,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        _graphCaptionText = new TextBlock
        {
            Margin = new Thickness(10, 8, 10, 0),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(186, 210, 235))
        };
        _graphMidLine = new Line
        {
            StrokeThickness = 1
        };
        _graphFill = new Polygon
        {
            StrokeThickness = 0
        };
        _graphLine = new Polyline
        {
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        _graphCanvas = new Canvas
        {
            Height = 76,
            Margin = new Thickness(10, 24, 10, 10),
            ClipToBounds = true,
            IsHitTestVisible = false
        };
        _graphCanvas.Children.Add(_graphMidLine);
        _graphCanvas.Children.Add(_graphFill);
        _graphCanvas.Children.Add(_graphLine);

        var graphGrid = new Grid();
        graphGrid.Children.Add(_graphCanvas);
        graphGrid.Children.Add(_graphCaptionText);
        _graphBorder = new Border
        {
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(0, 10, 0, 0),
            Child = graphGrid
        };

        _metricChips =
        [
            new OverlayMetricChip(),
            new OverlayMetricChip(),
            new OverlayMetricChip(),
            new OverlayMetricChip(),
            new OverlayMetricChip(),
            new OverlayMetricChip()
        ];
        _metricsPanel = new WrapPanel
        {
            Margin = new Thickness(0, 10, 0, 0)
        };
        foreach (var chip in _metricChips)
        {
            _metricsPanel.Children.Add(chip.Element);
        }

        _detailText = new TextBlock
        {
            Foreground = new SolidColorBrush(MediaColor.FromRgb(173, 191, 210)),
            FontSize = 13,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_headerText, 0);
        Grid.SetColumn(_modeBadge, 1);
        headerGrid.Children.Add(_headerText);
        headerGrid.Children.Add(_modeBadge);

        var contentStack = new StackPanel();
        contentStack.Children.Add(headerGrid);
        contentStack.Children.Add(_fpsText);
        contentStack.Children.Add(_summaryText);
        contentStack.Children.Add(_graphBorder);
        contentStack.Children.Add(_metricsPanel);
        contentStack.Children.Add(_detailText);

        _accentBar = new Border
        {
            Visibility = Visibility.Collapsed,
            Height = 4,
            CornerRadius = new CornerRadius(18, 18, 0, 0)
        };

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_accentBar, 0);
        Grid.SetRow(contentStack, 1);
        rootGrid.Children.Add(_accentBar);
        rootGrid.Children.Add(contentStack);

        _cardBorder = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(18, 14, 18, 16),
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(120, 116, 172, 226)),
            BorderThickness = new Thickness(1),
            Child = rootGrid
        };

        Content = _cardBorder;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        extendedStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(extendedStyle));
    }

    public void ApplyConfiguration(PerformanceSettingsConfiguration configuration)
    {
        Width = Math.Max(220d, configuration.OverlayWidth);

        var scale = Math.Clamp(configuration.OverlayScale / 100d, 0.8d, 1.6d);
        var palette = ResolvePalette(configuration.BackgroundTheme);
        var alpha = (byte)Math.Clamp((int)Math.Round(configuration.BackgroundOpacity * 2.55d), 0, 255);
        _cardBorder.Background = new SolidColorBrush(MediaColor.FromArgb(alpha, palette.Background.R, palette.Background.G, palette.Background.B));
        _cardBorder.BorderBrush = new SolidColorBrush(MediaColor.FromArgb(170, palette.Border.R, palette.Border.G, palette.Border.B));
        _cardBorder.CornerRadius = new CornerRadius(18d * scale);
        _cardBorder.Padding = new Thickness(16d * scale, 12d * scale, 16d * scale, 14d * scale);

        _accentBar.Height = Math.Max(3d, 4d * scale);
        _accentBar.CornerRadius = new CornerRadius(18d * scale, 18d * scale, 0, 0);
        _accentBar.Background = new SolidColorBrush(palette.Accent);

        _headerText.FontSize = 16d * scale;
        _headerText.Foreground = new SolidColorBrush(palette.Text);
        _modeBadge.Padding = new Thickness(10d * scale, 4d * scale, 10d * scale, 4d * scale);
        _modeBadge.Background = new SolidColorBrush(MediaColor.FromArgb(82, palette.Accent.R, palette.Accent.G, palette.Accent.B));
        _modeBadge.BorderBrush = new SolidColorBrush(MediaColor.FromArgb(110, palette.Accent.R, palette.Accent.G, palette.Accent.B));
        _modeBadge.BorderThickness = new Thickness(1);
        _modeBadgeText.FontSize = 11d * scale;
        _modeBadgeText.Foreground = new SolidColorBrush(palette.Text);

        _fpsText.FontSize = 38d * scale;
        _fpsText.Foreground = new SolidColorBrush(palette.Text);
        _summaryText.FontSize = 14d * scale;
        _summaryText.Foreground = new SolidColorBrush(palette.SubText);
        _detailText.FontSize = 13d * scale;
        _detailText.Foreground = new SolidColorBrush(palette.MutedText);

        _graphBorder.CornerRadius = new CornerRadius(14d * scale);
        _graphBorder.Background = new SolidColorBrush(MediaColor.FromArgb(68, palette.Surface.R, palette.Surface.G, palette.Surface.B));
        _graphBorder.BorderBrush = new SolidColorBrush(MediaColor.FromArgb(100, palette.Border.R, palette.Border.G, palette.Border.B));
        _graphBorder.BorderThickness = new Thickness(1);
        _graphCaptionText.FontSize = 11d * scale;
        _graphCaptionText.Foreground = new SolidColorBrush(palette.SubText);
        _graphCanvas.Height = 76d * scale;
        _graphCanvas.Margin = new Thickness(10d * scale, 24d * scale, 10d * scale, 10d * scale);
        _graphLine.Stroke = new SolidColorBrush(palette.Accent);
        _graphFill.Fill = new SolidColorBrush(MediaColor.FromArgb(82, palette.Accent.R, palette.Accent.G, palette.Accent.B));
        _graphMidLine.Stroke = new SolidColorBrush(MediaColor.FromArgb(90, palette.Border.R, palette.Border.G, palette.Border.B));

        foreach (var chip in _metricChips)
        {
            chip.ApplyPalette(palette, scale);
        }
    }

    public void Render(
        PerformanceSettingsConfiguration configuration,
        PerformanceRuntimeStatus status,
        OverlayMetricsSnapshot metrics)
    {
        ApplyConfiguration(configuration);

        var processLabel = !string.IsNullOrWhiteSpace(status.TargetProcessName)
            ? status.TargetProcessName
            : "TFS FPS Overlay";
        _headerText.Text = processLabel;
        _modeBadgeText.Text = BuildModeBadgeText(configuration);

        if (!string.IsNullOrWhiteSpace(status.ErrorText))
        {
            _fpsText.Text = "No Data";
            _summaryText.Text = "ETW frame capture is waiting for the elevated helper.";
            _detailText.Text = status.ErrorText;
            _graphBorder.Visibility = Visibility.Collapsed;
            SetMetricVisibility(0);
        }
        else if (status.TargetProcessId <= 0)
        {
            _fpsText.Text = "-- FPS";
            _summaryText.Text = "Waiting for a game window";
            _detailText.Text = status.DetailText;
            _graphBorder.Visibility = Visibility.Collapsed;
            SetMetricVisibility(0);
        }
        else
        {
            _fpsText.Text = status.FramesPerSecond > 0d
                ? $"{status.FramesPerSecond:0.#} FPS"
                : "-- FPS";
            _summaryText.Text = BuildSummaryText(configuration, status, metrics);
            _detailText.Text = configuration.OverlayLevel switch
            {
                0 => status.TargetWindowTitle,
                1 => $"{status.TargetWindowTitle}\nAuto target {(configuration.AutoTargetEnabled ? "On" : "Off")}  •  {metrics.SampleCount} samples",
                _ => $"{status.TargetWindowTitle}\nCPU {status.TargetCpuPercent:0.#}%  •  RAM {status.TargetMemoryMb} MB  •  Pacing {status.FramePacingMs:0.0} ms"
            };

            RenderMetricChips(configuration, status, metrics);
            UpdateGraph(configuration, status, metrics);
        }

        ApplyModeVisibility(configuration, status);
        UpdateLayout();
        PositionWindow(configuration.OverlayPosition);
    }

    private static string BuildModeBadgeText(PerformanceSettingsConfiguration configuration)
    {
        var levelText = configuration.OverlayLevel switch
        {
            0 => "Minimal",
            1 => "Balanced",
            _ => "Detailed"
        };

        if (configuration.OverlayLevel == 0 || configuration.GraphMode == 0)
        {
            return levelText;
        }

        var graphText = configuration.GraphMode == 2 ? "Frametime" : "FPS";
        return $"{levelText} • {graphText}";
    }

    private static string BuildSummaryText(
        PerformanceSettingsConfiguration configuration,
        PerformanceRuntimeStatus status,
        OverlayMetricsSnapshot metrics)
    {
        return configuration.OverlayLevel switch
        {
            0 => $"{status.FrameTimeMs:0.0} ms frametime  •  1% low {status.OnePercentLowFps:0.#}",
            1 => $"{status.FrameTimeMs:0.0} ms  •  1% low {status.OnePercentLowFps:0.#}  •  CPU {status.TargetCpuPercent:0.#}%",
            _ => $"{status.FrameTimeMs:0.0} ms  •  1% low {status.OnePercentLowFps:0.#}  •  {metrics.SampleCount} live samples"
        };
    }

    private void RenderMetricChips(
        PerformanceSettingsConfiguration configuration,
        PerformanceRuntimeStatus status,
        OverlayMetricsSnapshot metrics)
    {
        if (configuration.OverlayLevel == 0)
        {
            SetMetricVisibility(0);
            return;
        }

        var entries = configuration.OverlayLevel switch
        {
            1 => new (string Label, string Value)[]
            {
                ("Frame", $"{status.FrameTimeMs:0.0} ms"),
                ("1% Low", $"{status.OnePercentLowFps:0.#}"),
                ("CPU", $"{status.TargetCpuPercent:0.#}%"),
                ("RAM", $"{status.TargetMemoryMb} MB")
            },
            _ => new (string Label, string Value)[]
            {
                ("Frame", $"{status.FrameTimeMs:0.0} ms"),
                ("1% Low", $"{status.OnePercentLowFps:0.#}"),
                ("Pace", $"{status.FramePacingMs:0.0} ms"),
                ("CPU", $"{status.TargetCpuPercent:0.#}%"),
                ("RAM", $"{status.TargetMemoryMb} MB"),
                ("Samples", metrics.SampleCount.ToString())
            }
        };

        for (var index = 0; index < _metricChips.Length; index += 1)
        {
            if (index < entries.Length)
            {
                _metricChips[index].Set(entries[index].Label, entries[index].Value);
                _metricChips[index].Element.Visibility = Visibility.Visible;
            }
            else
            {
                _metricChips[index].Element.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void SetMetricVisibility(int visibleCount)
    {
        for (var index = 0; index < _metricChips.Length; index += 1)
        {
            _metricChips[index].Element.Visibility = index < visibleCount
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void ApplyModeVisibility(PerformanceSettingsConfiguration configuration, PerformanceRuntimeStatus status)
    {
        var showDetails = !string.IsNullOrWhiteSpace(status.ErrorText) ||
                          status.TargetProcessId <= 0 ||
                          configuration.OverlayLevel >= 1;
        _detailText.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
        _metricsPanel.Visibility = configuration.OverlayLevel == 0 || status.TargetProcessId <= 0 || !string.IsNullOrWhiteSpace(status.ErrorText)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateGraph(
        PerformanceSettingsConfiguration configuration,
        PerformanceRuntimeStatus status,
        OverlayMetricsSnapshot metrics)
    {
        var showGraph = configuration.OverlayLevel >= 1 &&
                        configuration.GraphMode > 0 &&
                        string.IsNullOrWhiteSpace(status.ErrorText) &&
                        status.TargetProcessId > 0;
        _graphBorder.Visibility = showGraph ? Visibility.Visible : Visibility.Collapsed;
        if (!showGraph)
        {
            _graphLine.Points = new PointCollection();
            _graphFill.Points = new PointCollection();
            return;
        }

        UpdateLayout();
        var sampleSource = configuration.GraphMode == 2
            ? metrics.RecentFrameTimesMs
            : metrics.RecentFpsSamples;
        if (sampleSource.Count < 2)
        {
            _graphCaptionText.Text = configuration.GraphMode == 2
                ? "FRAMETIME GRAPH - waiting for samples"
                : "FPS GRAPH - waiting for samples";
            _graphLine.Points = new PointCollection();
            _graphFill.Points = new PointCollection();
            return;
        }

        _graphCaptionText.Text = configuration.GraphMode == 2
            ? "FRAMETIME GRAPH"
            : "FPS GRAPH";

        var width = Math.Max(40d, _graphCanvas.ActualWidth);
        var height = Math.Max(24d, _graphCanvas.ActualHeight);
        _graphCanvas.Width = width;
        _graphCanvas.Height = height;
        _graphMidLine.X1 = 0d;
        _graphMidLine.X2 = width;
        _graphMidLine.Y1 = height * 0.55d;
        _graphMidLine.Y2 = height * 0.55d;

        var samples = sampleSource.ToArray();
        var minValue = configuration.GraphMode == 1
            ? Math.Max(0d, samples.Min() * 0.85d)
            : Math.Max(0d, samples.Min() * 0.9d);
        var maxValue = configuration.GraphMode == 1
            ? Math.Max(samples.Max() * 1.05d, 30d)
            : Math.Max(samples.Max() * 1.15d, 16.6d);
        if (maxValue - minValue < 0.001d)
        {
            maxValue = minValue + 1d;
        }

        var points = new PointCollection();
        var fillPoints = new PointCollection
        {
            new System.Windows.Point(0d, height)
        };
        for (var index = 0; index < samples.Length; index += 1)
        {
            var x = samples.Length == 1
                ? width / 2d
                : width * index / (samples.Length - 1d);
            var normalized = Math.Clamp((samples[index] - minValue) / (maxValue - minValue), 0d, 1d);
            var y = height - normalized * height;
            var point = new System.Windows.Point(x, y);
            points.Add(point);
            fillPoints.Add(point);
        }

        fillPoints.Add(new System.Windows.Point(width, height));
        _graphLine.Points = points;
        _graphFill.Points = fillPoints;
    }

    private static OverlayPalette ResolvePalette(int backgroundTheme)
    {
        return backgroundTheme switch
        {
            1 => new OverlayPalette(
                MediaColor.FromRgb(28, 33, 43),
                MediaColor.FromRgb(52, 60, 75),
                MediaColor.FromRgb(95, 118, 150),
                MediaColor.FromRgb(114, 184, 255),
                MediaColor.FromRgb(209, 226, 242),
                MediaColor.FromRgb(245, 249, 253),
                MediaColor.FromRgb(202, 215, 229),
                MediaColor.FromRgb(161, 178, 197)),
            2 => new OverlayPalette(
                MediaColor.FromRgb(11, 15, 21),
                MediaColor.FromRgb(22, 28, 38),
                MediaColor.FromRgb(77, 99, 127),
                MediaColor.FromRgb(103, 176, 255),
                MediaColor.FromRgb(211, 227, 244),
                MediaColor.FromRgb(245, 249, 253),
                MediaColor.FromRgb(192, 208, 226),
                MediaColor.FromRgb(149, 167, 189)),
            3 => new OverlayPalette(
                MediaColor.FromRgb(22, 26, 33),
                MediaColor.FromRgb(39, 45, 56),
                MediaColor.FromRgb(103, 112, 134),
                MediaColor.FromRgb(151, 182, 255),
                MediaColor.FromRgb(224, 230, 244),
                MediaColor.FromRgb(245, 248, 252),
                MediaColor.FromRgb(204, 213, 228),
                MediaColor.FromRgb(165, 176, 194)),
            4 => new OverlayPalette(
                MediaColor.FromRgb(33, 49, 70),
                MediaColor.FromRgb(53, 71, 96),
                MediaColor.FromRgb(112, 159, 214),
                MediaColor.FromRgb(140, 210, 255),
                MediaColor.FromRgb(224, 239, 248),
                MediaColor.FromRgb(246, 250, 253),
                MediaColor.FromRgb(201, 218, 231),
                MediaColor.FromRgb(161, 181, 199)),
            _ => new OverlayPalette(
                MediaColor.FromRgb(44, 53, 84),
                MediaColor.FromRgb(61, 72, 110),
                MediaColor.FromRgb(112, 160, 226),
                MediaColor.FromRgb(127, 199, 255),
                MediaColor.FromRgb(220, 236, 249),
                MediaColor.FromRgb(246, 250, 253),
                MediaColor.FromRgb(200, 218, 236),
                MediaColor.FromRgb(160, 180, 200))
        };
    }

    private void PositionWindow(int overlayPosition)
    {
        var bounds = SystemParameters.WorkArea;
        const double margin = 22d;
        var left = overlayPosition switch
        {
            1 or 3 => bounds.Right - Width - margin,
            _ => bounds.Left + margin
        };
        var top = overlayPosition switch
        {
            2 or 3 => bounds.Bottom - ActualHeight - margin,
            _ => bounds.Top + margin
        };

        Left = Math.Max(bounds.Left, left);
        Top = Math.Max(bounds.Top, top);
    }

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20L;
    private const long WsExToolWindow = 0x80L;
    private const long WsExNoActivate = 0x08000000L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}

internal sealed class OverlayMetricChip
{
    private readonly TextBlock _labelText;
    private readonly TextBlock _valueText;

    public OverlayMetricChip()
    {
        _labelText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold
        };
        _valueText = new TextBlock
        {
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var stack = new StackPanel();
        stack.Children.Add(_labelText);
        stack.Children.Add(_valueText);

        Element = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 8, 8),
            Child = stack
        };
    }

    public Border Element { get; }

    public void ApplyPalette(OverlayPalette palette, double scale)
    {
        Element.MinWidth = 82d * scale;
        Element.Padding = new Thickness(10d * scale, 8d * scale, 10d * scale, 8d * scale);
        Element.CornerRadius = new CornerRadius(12d * scale);
        Element.Background = new SolidColorBrush(MediaColor.FromArgb(72, palette.Surface.R, palette.Surface.G, palette.Surface.B));
        Element.BorderBrush = new SolidColorBrush(MediaColor.FromArgb(105, palette.Border.R, palette.Border.G, palette.Border.B));
        Element.BorderThickness = new Thickness(1);
        _labelText.FontSize = 10d * scale;
        _labelText.Foreground = new SolidColorBrush(palette.MutedText);
        _valueText.FontSize = 14d * scale;
        _valueText.Foreground = new SolidColorBrush(palette.Text);
    }

    public void Set(string label, string value)
    {
        _labelText.Text = label.ToUpperInvariant();
        _valueText.Text = value;
    }
}

internal sealed record OverlayPalette(
    MediaColor Background,
    MediaColor Surface,
    MediaColor Border,
    MediaColor Accent,
    MediaColor AccentSoft,
    MediaColor Text,
    MediaColor SubText,
    MediaColor MutedText);
