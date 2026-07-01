using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace SteamLoader.App.Infrastructure.Performance;

internal sealed class SteamOsPerformanceOverlayWindow : Window
{
    private static readonly MediaFontFamily OverlayFont = new("Bahnschrift SemiCondensed");
    private static readonly MediaColor FpsRed = MediaColor.FromRgb(255, 94, 102);
    private static readonly MediaColor CpuBlue = MediaColor.FromRgb(54, 174, 255);
    private static readonly MediaColor RamPink = MediaColor.FromRgb(245, 142, 195);
    private static readonly MediaColor LowGreen = MediaColor.FromRgb(117, 245, 90);
    private static readonly MediaColor PaceOrange = MediaColor.FromRgb(255, 184, 92);
    private static readonly MediaColor AppGreen = MediaColor.FromRgb(49, 201, 118);
    private static readonly MediaColor BatteryOrange = MediaColor.FromRgb(255, 154, 88);

    private readonly Border _rootBorder;
    private readonly WrapPanel _stripPanel;
    private readonly StripMetric[] _stripMetrics;
    private readonly StackPanel _detailsPanel;
    private readonly TextBlock _metaPrimaryText;
    private readonly TextBlock _metaSecondaryText;
    private readonly StackPanel _rowsPanel;
    private readonly MetricRow[] _metricRows;
    private readonly Grid _graphHeaderGrid;
    private readonly TextBlock _graphCaptionText;
    private readonly TextBlock _graphSummaryText;
    private readonly Canvas _graphCanvas;
    private readonly Line _graphGuideLine;
    private readonly Polyline _graphLine;
    private readonly TextBlock _footerText;

    private Palette _activePalette = ResolvePalette(0);
    private double _activeScale = 1d;

    public SteamOsPerformanceOverlayWindow()
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
        UseLayoutRounding = true;

        _stripMetrics =
        [
            new StripMetric(),
            new StripMetric(),
            new StripMetric(),
            new StripMetric(),
            new StripMetric(),
            new StripMetric(),
            new StripMetric(),
            new StripMetric()
        ];
        _stripPanel = new WrapPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal
        };
        foreach (var metric in _stripMetrics)
        {
            _stripPanel.Children.Add(metric.Element);
        }

        _metaPrimaryText = new TextBlock
        {
            FontFamily = OverlayFont,
            FontWeight = FontWeights.Bold,
            Foreground = MediaBrushes.White
        };
        _metaSecondaryText = new TextBlock
        {
            FontFamily = OverlayFont,
            FontWeight = FontWeights.SemiBold,
            Foreground = MediaBrushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _metricRows =
        [
            new MetricRow(),
            new MetricRow(),
            new MetricRow(),
            new MetricRow(),
            new MetricRow(),
            new MetricRow(),
            new MetricRow(),
            new MetricRow()
        ];
        _rowsPanel = new StackPanel();
        foreach (var row in _metricRows)
        {
            _rowsPanel.Children.Add(row.Element);
        }

        _graphCaptionText = new TextBlock
        {
            FontFamily = OverlayFont,
            FontWeight = FontWeights.SemiBold
        };
        _graphSummaryText = new TextBlock
        {
            FontFamily = OverlayFont,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Right
        };

        _graphHeaderGrid = new Grid();
        _graphHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _graphHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_graphCaptionText, 0);
        Grid.SetColumn(_graphSummaryText, 1);
        _graphHeaderGrid.Children.Add(_graphCaptionText);
        _graphHeaderGrid.Children.Add(_graphSummaryText);

        _graphGuideLine = new Line
        {
            StrokeThickness = 1
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
            ClipToBounds = true,
            IsHitTestVisible = false
        };
        _graphCanvas.Children.Add(_graphGuideLine);
        _graphCanvas.Children.Add(_graphLine);

        _footerText = new TextBlock
        {
            FontFamily = OverlayFont,
            Foreground = MediaBrushes.White,
            TextWrapping = TextWrapping.Wrap
        };

        _detailsPanel = new StackPanel();
        _detailsPanel.Children.Add(_metaPrimaryText);
        _detailsPanel.Children.Add(_metaSecondaryText);
        _detailsPanel.Children.Add(_rowsPanel);
        _detailsPanel.Children.Add(_graphHeaderGrid);
        _detailsPanel.Children.Add(_graphCanvas);
        _detailsPanel.Children.Add(_footerText);

        var rootLayout = new Grid();
        rootLayout.Children.Add(_stripPanel);
        rootLayout.Children.Add(_detailsPanel);

        _rootBorder = new Border
        {
            Child = rootLayout
        };

        Content = _rootBorder;
        TextOptions.SetTextFormattingMode(_rootBorder, TextFormattingMode.Display);
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
        _activeScale = Math.Clamp(configuration.OverlayScale / 100d, 0.8d, 1.6d);
        _activePalette = ResolvePalette(configuration.BackgroundTheme);

        var minimumWidth = configuration.OverlayLevel switch
        {
            0 => 420d,
            1 => 290d,
            _ => 320d
        };
        var widthLimit = Math.Max(240d, SystemParameters.WorkArea.Width - 12d);
        Width = Math.Min(widthLimit, Math.Max(minimumWidth, configuration.OverlayWidth));

        var opacityFactor = configuration.OverlayLevel == 0 ? 0.35d : 0.72d;
        var alpha = (byte)Math.Clamp((int)Math.Round(configuration.BackgroundOpacity * 2.55d * opacityFactor), 0, 255);
        _rootBorder.Background = new SolidColorBrush(MediaColor.FromArgb(alpha, _activePalette.Background.R, _activePalette.Background.G, _activePalette.Background.B));
        _rootBorder.BorderBrush = new SolidColorBrush(MediaColor.FromArgb((byte)Math.Clamp(alpha + 24, 0, 190), _activePalette.Border.R, _activePalette.Border.G, _activePalette.Border.B));
        _rootBorder.BorderThickness = alpha > 12
            ? new Thickness(configuration.OverlayLevel == 0 ? 0.5d : 1d)
            : new Thickness(0d);
        _rootBorder.CornerRadius = new CornerRadius(configuration.OverlayLevel == 0 ? 5d * _activeScale : 8d * _activeScale);
        _rootBorder.Padding = configuration.OverlayLevel == 0
            ? new Thickness(8d * _activeScale, 3d * _activeScale, 8d * _activeScale, 4d * _activeScale)
            : new Thickness(10d * _activeScale, 8d * _activeScale, 10d * _activeScale, 9d * _activeScale);

        _stripPanel.MaxWidth = Math.Max(200d, Width - _rootBorder.Padding.Left - _rootBorder.Padding.Right);
        _metaPrimaryText.FontSize = configuration.OverlayLevel == 2 ? 17d * _activeScale : 13d * _activeScale;
        _metaSecondaryText.FontSize = configuration.OverlayLevel == 2 ? 13d * _activeScale : 11d * _activeScale;
        _metaSecondaryText.Foreground = new SolidColorBrush(_activePalette.SubText);
        _metaSecondaryText.Margin = new Thickness(0d, 0d, 0d, 4d * _activeScale);
        _graphHeaderGrid.Margin = new Thickness(0d, 6d * _activeScale, 0d, 0d);
        _graphCaptionText.FontSize = configuration.OverlayLevel == 2 ? 15d * _activeScale : 12d * _activeScale;
        _graphSummaryText.FontSize = configuration.OverlayLevel == 2 ? 13d * _activeScale : 11d * _activeScale;
        _graphSummaryText.Foreground = new SolidColorBrush(_activePalette.SubText);
        _graphCanvas.Height = configuration.OverlayLevel == 2 ? 78d * _activeScale : 64d * _activeScale;
        _graphCanvas.Margin = new Thickness(0d, 3d * _activeScale, 0d, 0d);
        _footerText.FontSize = configuration.OverlayLevel == 2 ? 12d * _activeScale : 11d * _activeScale;
        _footerText.Foreground = new SolidColorBrush(_activePalette.SubText);
        _footerText.Margin = new Thickness(0d, 6d * _activeScale, 0d, 0d);

        foreach (var metric in _stripMetrics)
        {
            metric.ApplyStyle(_activeScale);
        }
    }

    public void Render(
        PerformanceSettingsConfiguration configuration,
        PerformanceRuntimeStatus status,
        OverlayMetricsSnapshot metrics)
    {
        ApplyConfiguration(configuration);

        var batteryPercent = TryGetBatteryPercent();
        switch (configuration.OverlayLevel)
        {
            case 0:
                RenderStripPreset(status, batteryPercent);
                break;
            case 1:
                RenderFullPreset(configuration, status, metrics, batteryPercent);
                break;
            default:
                RenderCompactPreset(configuration, status, metrics, batteryPercent);
                break;
        }

        UpdateLayout();
        PositionWindow(configuration.OverlayPosition);
    }

    private void RenderStripPreset(PerformanceRuntimeStatus status, int? batteryPercent)
    {
        _stripPanel.Visibility = Visibility.Visible;
        _detailsPanel.Visibility = Visibility.Collapsed;

        var states = new List<StripMetricState>();
        if (!string.IsNullOrWhiteSpace(status.ErrorText))
        {
            states.Add(new StripMetricState("STATUS", "NO DATA", null, FpsRed, _activePalette.Text, true));
            states.Add(new StripMetricState("HELPER", "ETW", null, PaceOrange, _activePalette.Text, false));
        }
        else if (status.TargetProcessId <= 0)
        {
            states.Add(new StripMetricState("STATUS", "WAIT", null, FpsRed, _activePalette.Text, true));
            states.Add(new StripMetricState("APP", "READY", null, AppGreen, _activePalette.Text, false));
        }
        else
        {
            var memory = FormatMemoryValue(status.TargetMemoryMb);
            states.Add(new StripMetricState("FPS", FormatFpsValue(status.FramesPerSecond), "FPS", FpsRed, _activePalette.Text, true));
            states.Add(new StripMetricState("LOW", FormatFpsValue(status.OnePercentLowFps), "FPS", LowGreen, _activePalette.Text, false));
            states.Add(new StripMetricState("FRAME", FormatMsValue(status.FrameTimeMs), "MS", FpsRed, _activePalette.Text, false));
            states.Add(new StripMetricState("CPU", FormatPercentValue(status.TargetCpuPercent), "%", CpuBlue, _activePalette.Text, false));
            states.Add(new StripMetricState("RAM", memory.Value, memory.Unit, RamPink, _activePalette.Text, false));
            states.Add(new StripMetricState("PACE", FormatMsValue(status.FramePacingMs), "MS", PaceOrange, _activePalette.Text, false));
            if (batteryPercent is int battery)
            {
                states.Add(new StripMetricState("BATT", battery.ToString(), "%", BatteryOrange, _activePalette.Text, false));
            }
        }

        states.Add(new StripMetricState("TIME", DateTime.Now.ToString("HH:mm"), null, _activePalette.SubText, _activePalette.Text, false));
        ApplyStripMetrics(states);
    }

    private void RenderFullPreset(
        PerformanceSettingsConfiguration configuration,
        PerformanceRuntimeStatus status,
        OverlayMetricsSnapshot metrics,
        int? batteryPercent)
    {
        _stripPanel.Visibility = Visibility.Collapsed;
        _detailsPanel.Visibility = Visibility.Visible;
        _metaPrimaryText.Visibility = Visibility.Visible;
        _metaSecondaryText.Visibility = Visibility.Visible;
        _metaPrimaryText.Text = DateTime.Now.ToString("HH:mm:ss");
        _metaSecondaryText.Text = BuildMetaLine(status);

        if (!string.IsNullOrWhiteSpace(status.ErrorText))
        {
            ApplyMetricRows(
                configuration,
                [new MetricRowState("STATUS", "NO DATA", null, null, null, FpsRed, _activePalette.Text, _activePalette.Text, RowVariant.Hero)]);
            HideGraph();
            _footerText.Text = status.ErrorText;
            return;
        }

        if (status.TargetProcessId <= 0)
        {
            ApplyMetricRows(
                configuration,
                [new MetricRowState("STATUS", "WAIT", null, null, null, FpsRed, _activePalette.Text, _activePalette.Text, RowVariant.Hero)]);
            HideGraph();
            _footerText.Text = status.DetailText;
            return;
        }

        var memory = FormatMemoryValue(status.TargetMemoryMb);
        var rows = new List<MetricRowState>
        {
            new("FPS", FormatFpsValue(status.FramesPerSecond), "FPS", FormatMsValue(status.FrameTimeMs), "MS", FpsRed, _activePalette.Text, _activePalette.Text, RowVariant.Hero),
            new("1% LOW", FormatFpsValue(status.OnePercentLowFps), "FPS", null, null, LowGreen, _activePalette.Text, _activePalette.Text, RowVariant.Standard),
            new("CPU", FormatPercentValue(status.TargetCpuPercent), "%", null, null, CpuBlue, _activePalette.Text, _activePalette.Text, RowVariant.Standard),
            new("RAM", memory.Value, memory.Unit, null, null, RamPink, _activePalette.Text, _activePalette.Text, RowVariant.Standard),
            new("PACE", FormatMsValue(status.FramePacingMs), "MS", null, null, PaceOrange, _activePalette.Text, _activePalette.Text, RowVariant.Standard)
        };

        if (batteryPercent is int battery)
        {
            rows.Add(new MetricRowState("BATT", battery.ToString(), "%", null, null, BatteryOrange, _activePalette.Text, _activePalette.Text, RowVariant.Standard));
        }

        ApplyMetricRows(configuration, rows);
        RenderGraph(configuration, status, metrics);
        _footerText.Text = $"{BuildFooterTitle(status)} | Auto {(configuration.AutoTargetEnabled ? "On" : "Off")} | {metrics.SampleCount} samples";
    }

    private void RenderCompactPreset(
        PerformanceSettingsConfiguration configuration,
        PerformanceRuntimeStatus status,
        OverlayMetricsSnapshot metrics,
        int? batteryPercent)
    {
        _stripPanel.Visibility = Visibility.Collapsed;
        _detailsPanel.Visibility = Visibility.Visible;
        _metaPrimaryText.Visibility = Visibility.Collapsed;
        _metaSecondaryText.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(status.ErrorText))
        {
            ApplyMetricRows(
                configuration,
                [new MetricRowState("STATUS", "NO DATA", null, null, null, FpsRed, _activePalette.Text, _activePalette.Text, RowVariant.Compact)]);
            HideGraph();
            _footerText.Text = status.ErrorText;
            return;
        }

        if (status.TargetProcessId <= 0)
        {
            ApplyMetricRows(
                configuration,
                [new MetricRowState("STATUS", "WAIT", null, null, null, FpsRed, _activePalette.Text, _activePalette.Text, RowVariant.Compact)]);
            HideGraph();
            _footerText.Text = status.DetailText;
            return;
        }

        var memory = FormatMemoryValue(status.TargetMemoryMb);
        var rows = new List<MetricRowState>
        {
            new("CPU", FormatPercentValue(status.TargetCpuPercent), "%", null, null, CpuBlue, _activePalette.Text, _activePalette.Text, RowVariant.Compact),
            new("RAM", memory.Value, memory.Unit, null, null, RamPink, _activePalette.Text, _activePalette.Text, RowVariant.Compact),
            new("FPS", FormatFpsValue(status.FramesPerSecond), "FPS", FormatMsValue(status.FrameTimeMs), "MS", FpsRed, _activePalette.Text, _activePalette.Text, RowVariant.Hero),
            new("LOW", FormatFpsValue(status.OnePercentLowFps), "FPS", null, null, LowGreen, _activePalette.Text, _activePalette.Text, RowVariant.Compact)
        };

        if (batteryPercent is int battery)
        {
            rows.Add(new MetricRowState("BATT", battery.ToString(), "%", null, null, BatteryOrange, _activePalette.Text, _activePalette.Text, RowVariant.Compact));
        }

        ApplyMetricRows(configuration, rows);
        RenderGraph(configuration, status, metrics);
        _footerText.Text = BuildFooterTitle(status);
    }

    private void ApplyStripMetrics(IReadOnlyList<StripMetricState> metrics)
    {
        for (var index = 0; index < _stripMetrics.Length; index += 1)
        {
            if (index < metrics.Count)
            {
                _stripMetrics[index].Element.Visibility = Visibility.Visible;
                _stripMetrics[index].Set(metrics[index], _activeScale);
            }
            else
            {
                _stripMetrics[index].Element.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ApplyMetricRows(
        PerformanceSettingsConfiguration configuration,
        IReadOnlyList<MetricRowState> rows)
    {
        for (var index = 0; index < _metricRows.Length; index += 1)
        {
            if (index < rows.Count)
            {
                _metricRows[index].Element.Visibility = Visibility.Visible;
                _metricRows[index].Set(rows[index], _activeScale, configuration.OverlayLevel == 2);
            }
            else
            {
                _metricRows[index].Element.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void RenderGraph(
        PerformanceSettingsConfiguration configuration,
        PerformanceRuntimeStatus status,
        OverlayMetricsSnapshot metrics)
    {
        var showGraph = configuration.GraphMode > 0 &&
                        string.IsNullOrWhiteSpace(status.ErrorText) &&
                        status.TargetProcessId > 0;
        if (!showGraph)
        {
            HideGraph();
            return;
        }

        _graphHeaderGrid.Visibility = Visibility.Visible;
        _graphCanvas.Visibility = Visibility.Visible;
        _graphCaptionText.Foreground = new SolidColorBrush(configuration.GraphMode == 2 ? FpsRed : PaceOrange);
        _graphSummaryText.Foreground = new SolidColorBrush(_activePalette.SubText);
        _graphGuideLine.Stroke = new SolidColorBrush(MediaColor.FromArgb(110, _activePalette.Guide.R, _activePalette.Guide.G, _activePalette.Guide.B));
        _graphLine.Stroke = new SolidColorBrush(configuration.GraphMode == 2 ? _activePalette.GraphFrametime : _activePalette.GraphFps);
        _graphLine.StrokeThickness = configuration.OverlayLevel == 2 ? 2.4d * _activeScale : 2d * _activeScale;

        var sampleSource = configuration.GraphMode == 2
            ? metrics.RecentFrameTimesMs
            : metrics.RecentFpsSamples;
        var unit = configuration.GraphMode == 2 ? "ms" : "FPS";
        _graphCaptionText.Text = configuration.GraphMode == 2 ? "Frametime" : "FPS";

        if (sampleSource.Count < 2)
        {
            _graphSummaryText.Text = "waiting for samples";
            _graphLine.Points = new PointCollection();
            return;
        }

        var samples = sampleSource.ToArray();
        var minimum = samples.Min();
        var maximum = samples.Max();
        var current = samples[^1];
        _graphSummaryText.Text = $"min {minimum:0.0}  max {maximum:0.0}  now {current:0.0} {unit}";

        UpdateLayout();
        var width = _graphCanvas.ActualWidth > 40d
            ? _graphCanvas.ActualWidth
            : Math.Max(120d, Width - _rootBorder.Padding.Left - _rootBorder.Padding.Right - 8d);
        var height = Math.Max(24d, _graphCanvas.ActualHeight);
        _graphCanvas.Width = width;
        _graphCanvas.Height = height;
        _graphGuideLine.X1 = 0d;
        _graphGuideLine.X2 = width;
        _graphGuideLine.Y1 = height * 0.55d;
        _graphGuideLine.Y2 = height * 0.55d;

        var minValue = configuration.GraphMode == 2
            ? Math.Max(0d, minimum * 0.9d)
            : Math.Max(0d, minimum * 0.85d);
        var maxValue = configuration.GraphMode == 2
            ? Math.Max(maximum * 1.15d, 16.6d)
            : Math.Max(maximum * 1.05d, 30d);
        if (maxValue - minValue < 0.001d)
        {
            maxValue = minValue + 1d;
        }

        var points = new PointCollection();
        for (var index = 0; index < samples.Length; index += 1)
        {
            var x = samples.Length == 1
                ? width / 2d
                : width * index / (samples.Length - 1d);
            var normalized = Math.Clamp((samples[index] - minValue) / (maxValue - minValue), 0d, 1d);
            var y = height - normalized * height;
            points.Add(new System.Windows.Point(x, y));
        }

        _graphLine.Points = points;
    }

    private void HideGraph()
    {
        _graphHeaderGrid.Visibility = Visibility.Collapsed;
        _graphCanvas.Visibility = Visibility.Collapsed;
        _graphLine.Points = new PointCollection();
    }

    private static string BuildMetaLine(PerformanceRuntimeStatus status)
    {
        if (!string.IsNullOrWhiteSpace(status.TargetProcessName))
        {
            return TrimOverlayText(status.TargetProcessName.ToUpperInvariant(), 28);
        }

        return "TOOLS FOR STEAM";
    }

    private static string BuildFooterTitle(PerformanceRuntimeStatus status)
    {
        var preferred = !string.IsNullOrWhiteSpace(status.TargetWindowTitle)
            ? status.TargetWindowTitle
            : status.TargetProcessName;
        return TrimOverlayText(preferred, 52);
    }

    private static string TrimOverlayText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Waiting for a target";
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        var safeLength = Math.Max(0, maxLength - 3);
        return $"{trimmed[..safeLength]}...";
    }

    private static string FormatFpsValue(double value)
    {
        if (value <= 0d)
        {
            return "--";
        }

        return value >= 100d ? value.ToString("0") : value.ToString("0.#");
    }

    private static string FormatMsValue(double value)
    {
        return value <= 0d ? "--" : value.ToString("0.0");
    }

    private static string FormatPercentValue(double value)
    {
        return value <= 0d ? "0" : value.ToString("0.#");
    }

    private static (string Value, string Unit) FormatMemoryValue(long memoryMb)
    {
        if (memoryMb >= 1024L)
        {
            return ($"{memoryMb / 1024d:0.0}", "GiB");
        }

        return (Math.Max(0L, memoryMb).ToString(), "MiB");
    }

    private static int? TryGetBatteryPercent()
    {
        try
        {
            if (!GetSystemPowerStatus(out var powerStatus))
            {
                return null;
            }

            if (powerStatus.BatteryFlag == 128 || powerStatus.BatteryLifePercent == 255)
            {
                return null;
            }

            return powerStatus.BatteryLifePercent;
        }
        catch
        {
            return null;
        }
    }

    private static Palette ResolvePalette(int backgroundTheme)
    {
        return backgroundTheme switch
        {
            1 => new Palette(
                MediaColor.FromRgb(16, 18, 24),
                MediaColor.FromRgb(58, 62, 72),
                MediaColor.FromRgb(244, 244, 244),
                MediaColor.FromRgb(198, 198, 198),
                MediaColor.FromRgb(114, 114, 114),
                MediaColor.FromRgb(0, 255, 77),
                MediaColor.FromRgb(255, 198, 87),
                MediaColor.FromRgb(88, 88, 88)),
            2 => new Palette(
                MediaColor.FromRgb(8, 10, 14),
                MediaColor.FromRgb(45, 48, 56),
                MediaColor.FromRgb(250, 250, 250),
                MediaColor.FromRgb(210, 210, 210),
                MediaColor.FromRgb(122, 122, 122),
                MediaColor.FromRgb(0, 255, 77),
                MediaColor.FromRgb(255, 195, 72),
                MediaColor.FromRgb(92, 92, 92)),
            3 => new Palette(
                MediaColor.FromRgb(20, 21, 26),
                MediaColor.FromRgb(68, 70, 80),
                MediaColor.FromRgb(247, 247, 247),
                MediaColor.FromRgb(204, 204, 204),
                MediaColor.FromRgb(126, 126, 126),
                MediaColor.FromRgb(0, 255, 77),
                MediaColor.FromRgb(255, 208, 112),
                MediaColor.FromRgb(102, 102, 102)),
            4 => new Palette(
                MediaColor.FromRgb(21, 25, 31),
                MediaColor.FromRgb(74, 84, 94),
                MediaColor.FromRgb(247, 249, 250),
                MediaColor.FromRgb(207, 214, 219),
                MediaColor.FromRgb(124, 134, 141),
                MediaColor.FromRgb(0, 255, 77),
                MediaColor.FromRgb(255, 207, 120),
                MediaColor.FromRgb(92, 102, 110)),
            _ => new Palette(
                MediaColor.FromRgb(14, 15, 18),
                MediaColor.FromRgb(52, 56, 64),
                MediaColor.FromRgb(246, 246, 246),
                MediaColor.FromRgb(204, 204, 204),
                MediaColor.FromRgb(120, 120, 120),
                MediaColor.FromRgb(0, 255, 77),
                MediaColor.FromRgb(255, 196, 84),
                MediaColor.FromRgb(90, 90, 90))
        };
    }

    private void PositionWindow(int overlayPosition)
    {
        var bounds = SystemParameters.WorkArea;
        var margin = Math.Max(8d, 10d * _activeScale);
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

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    private enum RowVariant
    {
        Standard,
        Hero,
        Compact
    }

    private sealed record StripMetricState(
        string Label,
        string Value,
        string? Unit,
        MediaColor LabelColor,
        MediaColor ValueColor,
        bool Emphasize);

    private sealed record MetricRowState(
        string Label,
        string PrimaryValue,
        string? PrimaryUnit,
        string? SecondaryValue,
        string? SecondaryUnit,
        MediaColor LabelColor,
        MediaColor PrimaryColor,
        MediaColor SecondaryColor,
        RowVariant Variant);

    private sealed record Palette(
        MediaColor Background,
        MediaColor Border,
        MediaColor Text,
        MediaColor SubText,
        MediaColor MutedText,
        MediaColor GraphFrametime,
        MediaColor GraphFps,
        MediaColor Guide);

    private sealed class StripMetric
    {
        private readonly TextBlock _labelText;
        private readonly TextBlock _valueText;
        private readonly TextBlock _unitText;

        public StripMetric()
        {
            _labelText = new TextBlock
            {
                FontFamily = OverlayFont,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            _valueText = new TextBlock
            {
                FontFamily = OverlayFont,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            _unitText = new TextBlock
            {
                FontFamily = OverlayFont,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            Element = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal
            };
            Element.Children.Add(_labelText);
            Element.Children.Add(_valueText);
            Element.Children.Add(_unitText);
        }

        public StackPanel Element { get; }

        public void ApplyStyle(double scale)
        {
            Element.Margin = new Thickness(0d, 0d, 14d * scale, 2d * scale);
        }

        public void Set(StripMetricState state, double scale)
        {
            _labelText.Text = string.IsNullOrWhiteSpace(state.Label) ? string.Empty : $"{state.Label} ";
            _labelText.Foreground = new SolidColorBrush(state.LabelColor);
            _labelText.FontSize = 16d * scale;

            _valueText.Text = state.Value;
            _valueText.Foreground = new SolidColorBrush(state.ValueColor);
            _valueText.FontSize = state.Emphasize ? 19d * scale : 17d * scale;

            _unitText.Text = string.IsNullOrWhiteSpace(state.Unit) ? string.Empty : $" {state.Unit}";
            _unitText.Foreground = new SolidColorBrush(state.ValueColor);
            _unitText.FontSize = 11d * scale;
            _unitText.Visibility = string.IsNullOrWhiteSpace(state.Unit)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private sealed class MetricRow
    {
        private readonly TextBlock _labelText;
        private readonly StackPanel _primaryPanel;
        private readonly TextBlock _primaryValueText;
        private readonly TextBlock _primaryUnitText;
        private readonly StackPanel _secondaryPanel;
        private readonly TextBlock _secondaryValueText;
        private readonly TextBlock _secondaryUnitText;

        public MetricRow()
        {
            _labelText = new TextBlock
            {
                FontFamily = OverlayFont,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            _primaryValueText = new TextBlock
            {
                FontFamily = OverlayFont,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            _primaryUnitText = new TextBlock
            {
                FontFamily = OverlayFont,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            _secondaryValueText = new TextBlock
            {
                FontFamily = OverlayFont,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            _secondaryUnitText = new TextBlock
            {
                FontFamily = OverlayFont,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            _primaryPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            _primaryPanel.Children.Add(_primaryValueText);
            _primaryPanel.Children.Add(_primaryUnitText);

            _secondaryPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            _secondaryPanel.Children.Add(_secondaryValueText);
            _secondaryPanel.Children.Add(_secondaryUnitText);

            Element = new Grid();
            Element.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Element.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Element.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_labelText, 0);
            Grid.SetColumn(_primaryPanel, 2);
            Grid.SetColumn(_secondaryPanel, 3);
            Element.Children.Add(_labelText);
            Element.Children.Add(_primaryPanel);
            Element.Children.Add(_secondaryPanel);
        }

        public Grid Element { get; }

        public void Set(MetricRowState state, double scale, bool compactLayout)
        {
            var labelMinWidth = compactLayout ? 118d * scale : 90d * scale;
            var labelSize = state.Variant switch
            {
                RowVariant.Compact => 28d * scale,
                RowVariant.Hero => 17d * scale,
                _ => 15d * scale
            };
            var primarySize = state.Variant switch
            {
                RowVariant.Compact => 34d * scale,
                RowVariant.Hero => 42d * scale,
                _ => 18d * scale
            };
            var primaryUnitSize = state.Variant switch
            {
                RowVariant.Compact => 16d * scale,
                RowVariant.Hero => 17d * scale,
                _ => 12d * scale
            };
            var secondarySize = state.Variant switch
            {
                RowVariant.Hero => 26d * scale,
                RowVariant.Compact => 18d * scale,
                _ => 16d * scale
            };
            var secondaryUnitSize = state.Variant switch
            {
                RowVariant.Hero => 14d * scale,
                RowVariant.Compact => 12d * scale,
                _ => 11d * scale
            };

            Element.Margin = state.Variant == RowVariant.Hero
                ? new Thickness(0d, 0d, 0d, 4d * scale)
                : new Thickness(0d, 0d, 0d, 2d * scale);

            _labelText.Text = state.Label;
            _labelText.MinWidth = labelMinWidth;
            _labelText.FontSize = labelSize;
            _labelText.Foreground = new SolidColorBrush(state.LabelColor);

            _primaryValueText.Text = state.PrimaryValue;
            _primaryValueText.FontSize = primarySize;
            _primaryValueText.Foreground = new SolidColorBrush(state.PrimaryColor);
            _primaryUnitText.Text = string.IsNullOrWhiteSpace(state.PrimaryUnit) ? string.Empty : $" {state.PrimaryUnit}";
            _primaryUnitText.FontSize = primaryUnitSize;
            _primaryUnitText.Foreground = new SolidColorBrush(state.PrimaryColor);
            _primaryUnitText.Visibility = string.IsNullOrWhiteSpace(state.PrimaryUnit)
                ? Visibility.Collapsed
                : Visibility.Visible;

            _secondaryValueText.Text = state.SecondaryValue ?? string.Empty;
            _secondaryValueText.FontSize = secondarySize;
            _secondaryValueText.Foreground = new SolidColorBrush(state.SecondaryColor);
            _secondaryUnitText.Text = string.IsNullOrWhiteSpace(state.SecondaryUnit) ? string.Empty : $" {state.SecondaryUnit}";
            _secondaryUnitText.FontSize = secondaryUnitSize;
            _secondaryUnitText.Foreground = new SolidColorBrush(state.SecondaryColor);
            _secondaryPanel.Visibility = string.IsNullOrWhiteSpace(state.SecondaryValue)
                ? Visibility.Collapsed
                : Visibility.Visible;
            _secondaryPanel.Margin = _secondaryPanel.Visibility == Visibility.Visible
                ? new Thickness(16d * scale, 0d, 0d, 0d)
                : new Thickness(0d);
        }
    }
}
