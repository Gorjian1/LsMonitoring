using Avalonia;
using Avalonia.Controls;
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

    static TrendPlot()
    {
        AffectsRender<TrendPlot>(ReadingsProperty, ExpectedIntervalSecondsProperty,
            WarningThresholdProperty, CriticalThresholdProperty);
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

    // Dark palette
    private static readonly Color BgCanvas = Color.FromRgb(0x0d, 0x11, 0x17);
    private static readonly Color BgSurface = Color.FromRgb(0x16, 0x1b, 0x22);
    private static readonly Color BorderSubtle = Color.FromRgb(0x30, 0x36, 0x3d);
    private static readonly Color TextMuted = Color.FromRgb(0x8b, 0x94, 0x9e);

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(BgSurface), bounds);

        var plot = new Rect(54, 18, Math.Max(1, bounds.Width - 72), Math.Max(1, bounds.Height - 52));
        context.DrawRectangle(null, new Pen(new SolidColorBrush(BorderSubtle), 1), plot);

        var readings = Readings;
        if (readings is null || readings.Count == 0)
        {
            DrawNoData(context, plot);
            return;
        }

        var minTime = readings.Min(x => x.Timestamp);
        var maxTime = readings.Max(x => x.Timestamp);
        if (maxTime <= minTime)
        {
            maxTime = minTime.AddSeconds(1);
        }

        var values = readings.SelectMany(x => new double?[] { x.Temperature, x.AAxis, x.BAxis })
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToList();

        if (values.Count == 0)
        {
            DrawNoData(context, plot);
            return;
        }

        var minY = values.Min();
        var maxY = values.Max();

        // Ensure thresholds are visible on the Y axis
        var warn = WarningThreshold;
        var crit = CriticalThreshold;
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
        DrawInvalidZones(context, readings, minTime, maxTime, plot, GapThresholdSeconds(readings));
        DrawGrid(context, plot);
        DrawAxisLabels(context, plot, minY, maxY);

        var gapThreshold = GapThresholdSeconds(readings);
        DrawSeries(context, readings, r => r.Temperature, minTime, maxTime, minY, maxY, plot, "#8b949e", 1.2, gapThreshold);
        DrawSeries(context, readings, r => r.AAxis, minTime, maxTime, minY, maxY, plot, "#2f81f7", 2, gapThreshold);
        DrawSeries(context, readings, r => r.BAxis, minTime, maxTime, minY, maxY, plot, "#8957e5", 2, gapThreshold);
        DrawTimeLabels(context, plot, minTime, maxTime);
        DrawLegend(context, bounds);
    }

    private static void DrawNoData(DrawingContext context, Rect plot)
    {
        DrawText(context, "No data", new Point(plot.Left + plot.Width / 2 - 28, plot.Top + plot.Height / 2 - 8), 13, "#8b949e");
    }

    private static void DrawThresholdBands(DrawingContext context, Rect plot, double minY, double maxY, double warn, double crit)
    {
        // Warning band: ±warn to ±crit (amber, very translucent)
        var warnBrush = new SolidColorBrush(Color.FromArgb(30, 0xd2, 0x99, 0x22));
        var critBrush = new SolidColorBrush(Color.FromArgb(30, 0xf8, 0x51, 0x49));

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
        FillBand(crit, maxY, critBrush);
        FillBand(minY, -crit, critBrush);

        // Warning bands (between ±warn and ±crit)
        FillBand(warn, crit, warnBrush);
        FillBand(-crit, -warn, warnBrush);

        // Threshold lines
        var warnPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 0xd2, 0x99, 0x22)), 1) { DashStyle = DashStyle.Dash };
        var critPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 0xf8, 0x51, 0x49)), 1) { DashStyle = DashStyle.Dash };

        void DrawLine(double value, Pen pen)
        {
            if (value < minY || value > maxY)
            {
                return;
            }

            var y = plot.Bottom - (value - minY) / (maxY - minY) * plot.Height;
            context.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        DrawLine(warn, warnPen);
        DrawLine(-warn, warnPen);
        DrawLine(crit, critPen);
        DrawLine(-crit, critPen);
    }

    private static void DrawInvalidZones(DrawingContext context, IReadOnlyList<Reading> readings,
        DateTime minTime, DateTime maxTime, Rect plot, double gapThresholdSeconds)
    {
        var totalSeconds = Math.Max(1, (maxTime - minTime).TotalSeconds);
        var brush = new SolidColorBrush(Color.FromArgb(45, 0x76, 0x83, 0x90));
        var dashPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 0x76, 0x83, 0x90)), 1) { DashStyle = DashStyle.Dash };

        DateTime? invalidStart = null;

        void FlushInvalidZone(DateTime end)
        {
            if (invalidStart is not { } s)
            {
                return;
            }

            var xStart = plot.Left + (s - minTime).TotalSeconds / totalSeconds * plot.Width;
            var xEnd = plot.Left + (end - minTime).TotalSeconds / totalSeconds * plot.Width;
            if (xEnd > xStart + 1)
            {
                context.FillRectangle(brush, new Rect(xStart, plot.Top, xEnd - xStart, plot.Height));
                context.DrawLine(dashPen, new Point(xStart, plot.Top), new Point(xStart, plot.Bottom));
                context.DrawLine(dashPen, new Point(xEnd, plot.Top), new Point(xEnd, plot.Bottom));
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
                var xS = plot.Left + (prev.Value - minTime).TotalSeconds / totalSeconds * plot.Width;
                var xE = plot.Left + (r.Timestamp - minTime).TotalSeconds / totalSeconds * plot.Width;
                context.FillRectangle(new SolidColorBrush(Color.FromArgb(25, 0x76, 0x83, 0x90)),
                    new Rect(xS, plot.Top, xE - xS, plot.Height));
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

    private static void DrawGrid(DrawingContext context, Rect plot)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(40, 0x30, 0x36, 0x3d)), 1);
        for (var i = 1; i < 5; i++)
        {
            var x = plot.Left + plot.Width * i / 5.0;
            var y = plot.Top + plot.Height * i / 5.0;
            context.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            context.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
        }
    }

    private static void DrawLegend(DrawingContext context, Rect bounds)
    {
        DrawLegendItem(context, bounds.Right - 220, 5, "#8b949e", "T");
        DrawLegendItem(context, bounds.Right - 165, 5, "#2f81f7", "A");
        DrawLegendItem(context, bounds.Right - 110, 5, "#8957e5", "B");
    }

    private static void DrawAxisLabels(DrawingContext context, Rect plot, double minY, double maxY)
    {
        DrawText(context, maxY.ToString("F1"), new Point(4, plot.Top - 7), 10, "#8b949e");
        var midY = (minY + maxY) / 2;
        DrawText(context, midY.ToString("F1"), new Point(4, plot.Top + plot.Height / 2 - 7), 10, "#8b949e");
        DrawText(context, minY.ToString("F1"), new Point(4, plot.Bottom - 12), 10, "#8b949e");
    }

    private static void DrawTimeLabels(DrawingContext context, Rect plot, DateTime minTime, DateTime maxTime)
    {
        DrawText(context, minTime.ToString("HH:mm"), new Point(plot.Left, plot.Bottom + 6), 10, "#8b949e");
        DrawText(context, maxTime.ToString("HH:mm"), new Point(plot.Right - 36, plot.Bottom + 6), 10, "#8b949e");
        var mid = minTime + (maxTime - minTime) / 2;
        DrawText(context, mid.ToString("HH:mm"), new Point(plot.Left + plot.Width / 2 - 18, plot.Bottom + 6), 10, "#8b949e");
    }

    private static void DrawLegendItem(DrawingContext context, double x, double y, string color, string label)
    {
        var brush = new SolidColorBrush(Color.Parse(color));
        context.FillRectangle(brush, new Rect(x, y + 5, 24, 2));
        DrawText(context, label, new Point(x + 30, y), 11, "#e6edf3");
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
        string color,
        double thickness,
        double gapThresholdSeconds)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var started = false;
            DateTime? previousTimestamp = null;
            foreach (var reading in readings)
            {
                if (reading.Invalid)
                {
                    started = false;
                    previousTimestamp = reading.Timestamp;
                    continue;
                }

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

        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse(color)), thickness), geometry);
    }

    private static Point Map(DateTime timestamp, double value, DateTime minTime, DateTime maxTime, double minY, double maxY, Rect plot)
    {
        var totalSeconds = Math.Max(1, (maxTime - minTime).TotalSeconds);
        var x = plot.Left + (timestamp - minTime).TotalSeconds / totalSeconds * plot.Width;
        var y = plot.Bottom - (value - minY) / (maxY - minY) * plot.Height;
        return new Point(x, Math.Clamp(y, plot.Top, plot.Bottom));
    }

    private static void DrawText(DrawingContext context, string textValue, Point point, double size, string color)
    {
        var text = new FormattedText(
            textValue,
            Thread.CurrentThread.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            size,
            new SolidColorBrush(Color.Parse(color)));
        context.DrawText(text, point);
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
}
