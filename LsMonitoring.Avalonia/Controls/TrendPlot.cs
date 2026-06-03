using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LsMonitoring.Core.Models;

namespace LsMonitoring.Avalonia.Controls;

public sealed class TrendPlot : Control
{
    public static readonly StyledProperty<IReadOnlyList<Reading>?> ReadingsProperty =
        AvaloniaProperty.Register<TrendPlot, IReadOnlyList<Reading>?>(nameof(Readings));

    public static readonly StyledProperty<double?> ExpectedIntervalSecondsProperty =
        AvaloniaProperty.Register<TrendPlot, double?>(nameof(ExpectedIntervalSeconds));

    public static readonly StyledProperty<double> WarningThresholdProperty =
        AvaloniaProperty.Register<TrendPlot, double>(nameof(WarningThreshold), 5.0);

    public static readonly StyledProperty<double> CriticalThresholdProperty =
        AvaloniaProperty.Register<TrendPlot, double>(nameof(CriticalThreshold), 10.0);

    public static readonly StyledProperty<string> SeriesProperty =
        AvaloniaProperty.Register<TrendPlot, string>(nameof(Series), "A");

    public static readonly StyledProperty<double> VisibleWindowSecondsProperty =
        AvaloniaProperty.Register<TrendPlot, double>(nameof(VisibleWindowSeconds), 3600);

    public static readonly StyledProperty<string> ValueModeProperty =
        AvaloniaProperty.Register<TrendPlot, string>(nameof(ValueMode), "absolute");

    public static readonly StyledProperty<double> ZeroAProperty =
        AvaloniaProperty.Register<TrendPlot, double>(nameof(ZeroA));

    public static readonly StyledProperty<double> ZeroBProperty =
        AvaloniaProperty.Register<TrendPlot, double>(nameof(ZeroB));

    public static readonly StyledProperty<DateTime?> CutoffTimeProperty =
        AvaloniaProperty.Register<TrendPlot, DateTime?>(nameof(CutoffTime));

    public static readonly StyledProperty<bool> IsPickingCutoffProperty =
        AvaloniaProperty.Register<TrendPlot, bool>(nameof(IsPickingCutoff));

    private Rect? _lastPlotRect;
    private DateTime? _lastMinTime;
    private DateTime? _lastMaxTime;

    static TrendPlot()
    {
        AffectsRender<TrendPlot>(ReadingsProperty, ExpectedIntervalSecondsProperty,
            WarningThresholdProperty, CriticalThresholdProperty, SeriesProperty, VisibleWindowSecondsProperty,
            ValueModeProperty, ZeroAProperty, ZeroBProperty, CutoffTimeProperty, IsPickingCutoffProperty);
    }

    public TrendPlot()
    {
        PointerPressed += OnPointerPressed;
    }

    public IReadOnlyList<Reading>? Readings
    {
        get => GetValue(ReadingsProperty);
        set => SetValue(ReadingsProperty, value);
    }

    public double? ExpectedIntervalSeconds
    {
        get => GetValue(ExpectedIntervalSecondsProperty);
        set => SetValue(ExpectedIntervalSecondsProperty, value);
    }

    public double WarningThreshold
    {
        get => GetValue(WarningThresholdProperty);
        set => SetValue(WarningThresholdProperty, value);
    }

    public double CriticalThreshold
    {
        get => GetValue(CriticalThresholdProperty);
        set => SetValue(CriticalThresholdProperty, value);
    }

    public string Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public double VisibleWindowSeconds
    {
        get => GetValue(VisibleWindowSecondsProperty);
        set => SetValue(VisibleWindowSecondsProperty, value);
    }

    public string ValueMode
    {
        get => GetValue(ValueModeProperty);
        set => SetValue(ValueModeProperty, value);
    }

    public double ZeroA
    {
        get => GetValue(ZeroAProperty);
        set => SetValue(ZeroAProperty, value);
    }

    public double ZeroB
    {
        get => GetValue(ZeroBProperty);
        set => SetValue(ZeroBProperty, value);
    }

    public DateTime? CutoffTime
    {
        get => GetValue(CutoffTimeProperty);
        set => SetValue(CutoffTimeProperty, value);
    }

    public bool IsPickingCutoff
    {
        get => GetValue(IsPickingCutoffProperty);
        set => SetValue(IsPickingCutoffProperty, value);
    }

    public event EventHandler<DateTime>? CutoffTimePicked;

    private const string TextHex = "#1C1E21";
    private const string TextMutedHex = "#7D8590";
    private const string AccentAHex = "#0EA5E9";
    private const string AccentBHex = "#7C3AED";
    private static readonly Color BgSurface = Color.FromRgb(0xff, 0xff, 0xff);
    private static readonly Color BorderSubtle = Color.FromRgb(0xd9, 0xde, 0xe5);

    // ── Cached brushes and pens — allocated once, reused on every Render call ──────────────
    // Avalonia's SolidColorBrush is mutable but we never mutate these after construction,
    // so sharing them across render passes is safe (rendering is always on the UI thread).
    private static readonly SolidColorBrush s_bgBrush         = new(BgSurface);
    private static readonly Pen             s_borderPen        = new(new SolidColorBrush(BorderSubtle), 1);

    private static readonly SolidColorBrush s_warnBandBrush    = new(Color.FromArgb(30, 0xd9, 0x77, 0x06));
    private static readonly SolidColorBrush s_critBandBrush    = new(Color.FromArgb(28, 0xdc, 0x26, 0x26));
    private static readonly Pen             s_warnLinePen      = new(new SolidColorBrush(Color.FromArgb(88, 0xd9, 0x77, 0x06)), 1) { DashStyle = DashStyle.Dash };
    private static readonly Pen             s_critLinePen      = new(new SolidColorBrush(Color.FromArgb(88, 0xdc, 0x26, 0x26)), 1) { DashStyle = DashStyle.Dash };

    private static readonly SolidColorBrush s_invalidBandBrush = new(Color.FromArgb(38, 0x8b, 0x91, 0x9a));
    private static readonly SolidColorBrush s_gapBandBrush     = new(Color.FromArgb(22, 0x8b, 0x91, 0x9a));
    private static readonly Pen             s_invalidDashPen   = new(new SolidColorBrush(Color.FromArgb(76, 0x8b, 0x91, 0x9a)), 1) { DashStyle = DashStyle.Dash };

    private static readonly Pen s_gridPen     = new(new SolidColorBrush(Color.FromArgb(105, 0xd9, 0xde, 0xe5)), 1);
    private static readonly Pen s_timeTickPen = new(new SolidColorBrush(Color.FromArgb(135, 0xd9, 0xde, 0xe5)), 1);

    private static readonly Pen s_cutoffPickingPen = new(new SolidColorBrush(Color.FromArgb(170, 0x0e, 0xa5, 0xe9)), 1.5);
    private static readonly Pen s_cutoffLinePen    = new(new SolidColorBrush(Color.FromArgb(180, 0x16, 0xa3, 0x4a)), 1.5) { DashStyle = DashStyle.Dash };

    private static readonly SolidColorBrush s_textBrush       = new(Color.Parse(TextHex));
    private static readonly SolidColorBrush s_textMutedBrush  = new(Color.Parse(TextMutedHex));
    private static readonly SolidColorBrush s_accentABrush    = new(Color.Parse(AccentAHex));
    private static readonly SolidColorBrush s_accentBBrush    = new(Color.Parse(AccentBHex));
    private static readonly Pen             s_accentAPen      = new(new SolidColorBrush(Color.Parse(AccentAHex)), 2);
    private static readonly Pen             s_accentBPen      = new(new SolidColorBrush(Color.Parse(AccentBHex)), 2);

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        context.FillRectangle(s_bgBrush, bounds);

        var plot = new Rect(54, 18, Math.Max(1, bounds.Width - 72), Math.Max(1, bounds.Height - 58));
        context.DrawRectangle(null, s_borderPen, plot);

        var readings = Readings;
        if (readings is null || readings.Count == 0)
        {
            ClearPointerMapping();
            DrawNoData(context, plot);
            return;
        }

        // ReadingBuffer.Merge always Sort()s after adding readings, so Readings is already
        // ordered by Timestamp — no need to re-sort here.
        var ordered = readings;

        var latestTime = ordered[^1].Timestamp;
        // Compute GapThresholdSeconds once; used by both DrawInvalidZones and DrawSeries below.
        var gapThreshold = GapThresholdSeconds(ordered);
        var rightPaddingSeconds = RightPaddingSeconds(ordered, gapThreshold);
        var maxTime = latestTime.AddSeconds(rightPaddingSeconds);
        var minTime = ResolveMinTime(ordered, maxTime);
        var windowReadings = ordered
            .Where(x => x.Timestamp >= minTime && x.Timestamp <= latestTime)
            .ToList();

        if (windowReadings.Count == 0)
        {
            windowReadings.Add(ordered[^1]);
            minTime = latestTime.AddSeconds(-Math.Max(60, rightPaddingSeconds * 6));
            maxTime = latestTime.AddSeconds(rightPaddingSeconds);
        }

        if (maxTime <= minTime)
        {
            maxTime = minTime.AddSeconds(1);
        }

        UpdatePointerMapping(plot, minTime, maxTime);

        var seriesReadings = windowReadings
            .Where(x => CutoffTime is not { } cutoff || x.Timestamp >= cutoff)
            .ToList();

        var values = seriesReadings
            .Select(SelectValue)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToList();

        // Ensure thresholds are visible on the Y axis
        var warn = WarningThreshold;
        var crit = CriticalThreshold;
        var minY = values.Count == 0 ? -crit - 0.5 : values.Min();
        var maxY = values.Count == 0 ? crit + 0.5 : values.Max();
        minY = Math.Min(minY, -crit - 0.5);
        maxY = Math.Max(maxY, crit + 0.5);

        if (Math.Abs(maxY - minY) < 0.001)
        {
            minY -= 1;
            maxY += 1;
        }
        else
        {
            var pad = (maxY - minY) * 0.06;
            minY -= pad;
            maxY += pad;
        }

        DrawThresholdBands(context, plot, minY, maxY, warn, crit);
        DrawInvalidZones(context, seriesReadings, minTime, maxTime, plot, gapThreshold);
        DrawGrid(context, plot, minTime, maxTime);
        DrawCutoffBlankArea(context, plot, minTime, maxTime);
        DrawCutoffMarker(context, plot, minTime, maxTime);
        DrawAxisLabels(context, plot, minY, maxY);

        if (values.Count == 0)
        {
            DrawNoData(context, plot);
        }
        else
        {
            DrawSeries(context, seriesReadings, SelectValue, minTime, maxTime, minY, maxY, plot, SeriesPen(), gapThreshold);
        }

        DrawTimeLabels(context, plot, minTime, maxTime);
        DrawLegend(context, bounds);
    }

    private static void DrawNoData(DrawingContext context, Rect plot)
    {
        DrawText(context, "Нет данных", new Point(plot.Left + plot.Width / 2 - 36, plot.Top + plot.Height / 2 - 8), 13, s_textMutedBrush);
    }

    private static void DrawThresholdBands(DrawingContext context, Rect plot, double minY, double maxY, double warn, double crit)
    {
        void FillBand(double lo, double hi, IBrush brush)
        {
            var loClamp = Math.Max(lo, minY);
            var hiClamp = Math.Min(hi, maxY);
            if (hiClamp <= loClamp)
            {
                return;
            }

            var yTop = plot.Bottom - (hiClamp - minY) / (maxY - minY) * plot.Height;
            var yBot = plot.Bottom - (loClamp - minY) / (maxY - minY) * plot.Height;
            context.FillRectangle(brush, new Rect(plot.Left, yTop, plot.Width, yBot - yTop));
        }

        // Critical bands (beyond ±crit)
        FillBand(crit, maxY, s_critBandBrush);
        FillBand(minY, -crit, s_critBandBrush);

        // Warning bands (between ±warn and ±crit)
        FillBand(warn, crit, s_warnBandBrush);
        FillBand(-crit, -warn, s_warnBandBrush);

        // Threshold lines
        void DrawLine(double value, Pen pen)
        {
            if (value < minY || value > maxY)
            {
                return;
            }

            var y = plot.Bottom - (value - minY) / (maxY - minY) * plot.Height;
            context.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        DrawLine(warn, s_warnLinePen);
        DrawLine(-warn, s_warnLinePen);
        DrawLine(crit, s_critLinePen);
        DrawLine(-crit, s_critLinePen);
    }

    private static void DrawInvalidZones(DrawingContext context, IReadOnlyList<Reading> readings,
        DateTime minTime, DateTime maxTime, Rect plot, double gapThresholdSeconds)
    {
        var totalSeconds = Math.Max(1, (maxTime - minTime).TotalSeconds);

        DateTime? invalidStart = null;

        void FlushInvalidZone(DateTime end)
        {
            if (invalidStart is not { } s)
            {
                return;
            }

            var xStart = MapX(s, minTime, maxTime, plot);
            var xEnd = MapX(end, minTime, maxTime, plot);
            if (xEnd > xStart + 1)
            {
                context.FillRectangle(s_invalidBandBrush, new Rect(xStart, plot.Top, xEnd - xStart, plot.Height));
                context.DrawLine(s_invalidDashPen, new Point(xStart, plot.Top), new Point(xStart, plot.Bottom));
                context.DrawLine(s_invalidDashPen, new Point(xEnd, plot.Top), new Point(xEnd, plot.Bottom));
            }

            invalidStart = null;
        }

        DateTime? prev = null;
        foreach (var r in readings)
        {
            if (prev is not null && (r.Timestamp - prev.Value).TotalSeconds > gapThresholdSeconds)
            {
                FlushInvalidZone(prev.Value);
                // Gap zone
                var xS = MapX(prev.Value, minTime, maxTime, plot);
                var xE = MapX(r.Timestamp, minTime, maxTime, plot);
                context.FillRectangle(s_gapBandBrush, new Rect(xS, plot.Top, xE - xS, plot.Height));
            }

            if (r.Invalid)
            {
                invalidStart ??= r.Timestamp;
            }
            else
            {
                FlushInvalidZone(r.Timestamp);
            }

            prev = r.Timestamp;
        }

        if (prev is not null)
        {
            FlushInvalidZone(prev.Value);
        }
    }

    private static void DrawGrid(DrawingContext context, Rect plot, DateTime minTime, DateTime maxTime)
    {
        for (var i = 1; i < 5; i++)
        {
            var y = plot.Top + plot.Height * i / 5.0;
            context.DrawLine(s_gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        foreach (var tick in GenerateTimeTicks(minTime, maxTime, plot.Width))
        {
            var x = MapX(tick, minTime, maxTime, plot);
            context.DrawLine(s_timeTickPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
    }

    private void DrawCutoffMarker(DrawingContext context, Rect plot, DateTime minTime, DateTime maxTime)
    {
        if (IsPickingCutoff)
        {
            context.DrawRectangle(null, s_cutoffPickingPen, plot);
        }

        if (CutoffTime is not { } cutoff || cutoff < minTime || cutoff > maxTime)
        {
            return;
        }

        var x = MapX(cutoff, minTime, maxTime, plot);
        context.DrawLine(s_cutoffLinePen, new Point(x, plot.Top), new Point(x, plot.Bottom));
    }

    private void DrawCutoffBlankArea(DrawingContext context, Rect plot, DateTime minTime, DateTime maxTime)
    {
        if (CutoffTime is not { } cutoff || cutoff <= minTime)
        {
            return;
        }

        var cutoffX = cutoff >= maxTime ? plot.Right : MapX(cutoff, minTime, maxTime, plot);
        if (cutoffX <= plot.Left)
        {
            return;
        }

        context.FillRectangle(
            s_bgBrush,
            new Rect(plot.Left + 1, plot.Top + 1, Math.Max(0, cutoffX - plot.Left - 1), Math.Max(0, plot.Height - 2)));
    }

    private void DrawLegend(DrawingContext context, Rect bounds)
    {
        DrawLegendItem(context, bounds.Right - 122, 5, SeriesBrush(), SeriesLabel());
    }

    private static void DrawAxisLabels(DrawingContext context, Rect plot, double minY, double maxY)
    {
        DrawText(context, maxY.ToString("F1"), new Point(4, plot.Top - 7), 10, s_textMutedBrush);
        var midY = (minY + maxY) / 2;
        DrawText(context, midY.ToString("F1"), new Point(4, plot.Top + plot.Height / 2 - 7), 10, s_textMutedBrush);
        DrawText(context, minY.ToString("F1"), new Point(4, plot.Bottom - 12), 10, s_textMutedBrush);
    }

    private static void DrawTimeLabels(DrawingContext context, Rect plot, DateTime minTime, DateTime maxTime)
    {
        var ticks = GenerateTimeTicks(minTime, maxTime, plot.Width).ToList();
        if (ticks.Count == 0)
        {
            return;
        }

        var format = TimeLabelFormat(minTime, maxTime, ticks);
        foreach (var tick in ticks)
        {
            var label = tick.ToString(format);
            var x = MapX(tick, minTime, maxTime, plot);
            var estimatedWidth = label.Length * 5.8;
            var labelX = Math.Clamp(x - estimatedWidth / 2, plot.Left, plot.Right - estimatedWidth);
            DrawText(context, label, new Point(labelX, plot.Bottom + 6), 10, s_textMutedBrush);
        }
    }

    private static void DrawLegendItem(DrawingContext context, double x, double y, IBrush brush, string label)
    {
        context.FillRectangle(brush, new Rect(x, y + 5, 24, 2));
        DrawText(context, label, new Point(x + 30, y), 11, s_textBrush);
    }

    private static void DrawSeries(
        DrawingContext context,
        IReadOnlyList<Reading> readings,
        Func<Reading, double?> selector,
        DateTime minTime,
        DateTime maxTime,
        double minY,
        double maxY,
        Rect plot,
        Pen seriesPen,
        double gapThresholdSeconds)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var started = false;
            DateTime? previousTimestamp = null;
            foreach (var reading in readings)
            {
                var value = selector(reading);
                if (!value.HasValue)
                {
                    started = false;
                    previousTimestamp = reading.Timestamp;
                    continue;
                }

                if (previousTimestamp is not null &&
                    (reading.Timestamp - previousTimestamp.Value).TotalSeconds > gapThresholdSeconds)
                {
                    started = false;
                }

                var point = Map(reading.Timestamp, value.Value, minTime, maxTime, minY, maxY, plot);
                if (!started)
                {
                    g.BeginFigure(point, false);
                    started = true;
                }
                else
                {
                    g.LineTo(point);
                }

                previousTimestamp = reading.Timestamp;
            }
        }

        context.DrawGeometry(null, seriesPen, geometry);
    }

    private static Point Map(DateTime timestamp, double value, DateTime minTime, DateTime maxTime, double minY, double maxY, Rect plot)
    {
        var x = MapX(timestamp, minTime, maxTime, plot);
        var y = plot.Bottom - (value - minY) / (maxY - minY) * plot.Height;
        return new Point(x, Math.Clamp(y, plot.Top, plot.Bottom));
    }

    private static double MapX(DateTime timestamp, DateTime minTime, DateTime maxTime, Rect plot)
    {
        var totalSeconds = Math.Max(1, (maxTime - minTime).TotalSeconds);
        var x = plot.Left + (timestamp - minTime).TotalSeconds / totalSeconds * plot.Width;
        return Math.Clamp(x, plot.Left, plot.Right);
    }

    private static void DrawText(DrawingContext context, string textValue, Point point, double size, IBrush brush)
    {
        var text = new FormattedText(
            textValue,
            Thread.CurrentThread.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            size,
            brush);
        context.DrawText(text, point);
    }

    private static IEnumerable<DateTime> GenerateTimeTicks(DateTime minTime, DateTime maxTime, double plotWidth)
    {
        var totalSeconds = Math.Max(1, (maxTime - minTime).TotalSeconds);
        var targetCount = Math.Clamp((int)Math.Floor(plotWidth / 92), 4, 10);
        var step = NiceTimeStep(totalSeconds / targetCount);
        var stepTicks = step.Ticks;
        var firstTicks = ((minTime.Ticks + stepTicks - 1) / stepTicks) * stepTicks;

        for (var ticks = firstTicks; ticks <= maxTime.Ticks; ticks += stepTicks)
        {
            var tick = new DateTime(ticks, minTime.Kind);
            if (tick >= minTime && tick <= maxTime)
            {
                yield return tick;
            }
        }
    }

    private static TimeSpan NiceTimeStep(double minimumSeconds)
    {
        var steps = new[]
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(2),
            TimeSpan.FromHours(3),
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(12),
            TimeSpan.FromDays(1),
            TimeSpan.FromDays(2),
            TimeSpan.FromDays(7)
        };

        return steps.FirstOrDefault(step => step.TotalSeconds >= minimumSeconds, steps[^1]);
    }

    private static string TimeLabelFormat(DateTime minTime, DateTime maxTime, IReadOnlyList<DateTime> ticks)
    {
        var span = maxTime - minTime;
        if (span.TotalMinutes <= 30 && ticks.Zip(ticks.Skip(1), (a, b) => (b - a).TotalSeconds).Any(x => x < 60))
        {
            return "HH:mm:ss";
        }

        return minTime.Date == maxTime.Date ? "HH:mm" : "dd.MM HH:mm";
    }

    private double GapThresholdSeconds(IReadOnlyList<Reading> readings)
    {
        if (ExpectedIntervalSeconds is { } expected)
        {
            return expected * 2.5;
        }

        if (readings.Count < 2)
        {
            return 150;
        }

        var deltas = readings.Zip(readings.Skip(1), (a, b) => (b.Timestamp - a.Timestamp).TotalSeconds)
            .Where(x => x > 0)
            .Order()
            .ToList();
        return deltas.Count == 0 ? 150 : deltas[deltas.Count / 2] * 2.5;
    }

    private DateTime ResolveMinTime(IReadOnlyList<Reading> orderedReadings, DateTime maxTime)
    {
        var minTime = orderedReadings[0].Timestamp;
        if (VisibleWindowSeconds > 0)
        {
            minTime = maxTime.AddSeconds(-VisibleWindowSeconds);
        }

        return minTime;
    }

    private double RightPaddingSeconds(IReadOnlyList<Reading> readings, double precomputedGapThreshold)
    {
        if (ExpectedIntervalSeconds is { } expected && expected > 0)
        {
            return Math.Clamp(expected, 2, 300);
        }

        return Math.Clamp(precomputedGapThreshold / 2.5, 2, 300);
    }

    private double? SelectValue(Reading reading)
    {
        var isB = string.Equals(Series, "B", StringComparison.OrdinalIgnoreCase);
        var rawValue = string.Equals(ValueMode, "variation", StringComparison.OrdinalIgnoreCase)
            ? (isB ? reading.BVariation : reading.AVariation)
            : (isB ? reading.BAxis : reading.AAxis);

        if (rawValue is not { } value)
        {
            return null;
        }

        return value - (isB ? ZeroB : ZeroA);
    }

    private IBrush SeriesBrush() =>
        string.Equals(Series, "B", StringComparison.OrdinalIgnoreCase) ? s_accentBBrush : s_accentABrush;

    private Pen SeriesPen() =>
        string.Equals(Series, "B", StringComparison.OrdinalIgnoreCase) ? s_accentBPen : s_accentAPen;

    private string SeriesLabel()
    {
        var prefix = string.Equals(ValueMode, "variation", StringComparison.OrdinalIgnoreCase) ? "Δ" : "";
        return string.Equals(Series, "B", StringComparison.OrdinalIgnoreCase)
            ? $"{prefix}B от 0"
            : $"{prefix}A от 0";
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsPickingCutoff ||
            _lastPlotRect is not { } plot ||
            _lastMinTime is not { } minTime ||
            _lastMaxTime is not { } maxTime)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (!plot.Contains(point))
        {
            return;
        }

        var picked = RoundToStep(TimeAtX(point.X, plot, minTime, maxTime), TimeSpan.FromSeconds(30));
        CutoffTimePicked?.Invoke(this, picked);
        e.Handled = true;
    }

    private static DateTime TimeAtX(double x, Rect plot, DateTime minTime, DateTime maxTime)
    {
        var ratio = Math.Clamp((x - plot.Left) / Math.Max(1, plot.Width), 0, 1);
        return minTime.AddSeconds((maxTime - minTime).TotalSeconds * ratio);
    }

    private static DateTime RoundToStep(DateTime value, TimeSpan step)
    {
        var ticks = ((value.Ticks + step.Ticks / 2) / step.Ticks) * step.Ticks;
        return new DateTime(ticks, value.Kind);
    }

    private void UpdatePointerMapping(Rect plot, DateTime minTime, DateTime maxTime)
    {
        _lastPlotRect = plot;
        _lastMinTime = minTime;
        _lastMaxTime = maxTime;
    }

    private void ClearPointerMapping()
    {
        _lastPlotRect = null;
        _lastMinTime = null;
        _lastMaxTime = null;
    }
}
