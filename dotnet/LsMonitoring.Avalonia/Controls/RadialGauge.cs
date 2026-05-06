using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LsMonitoring.Avalonia.Controls;

public sealed class RadialGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<RadialGauge, double>(nameof(Value), 0.0);

    public static readonly StyledProperty<double> MinValueProperty =
        AvaloniaProperty.Register<RadialGauge, double>(nameof(MinValue), -15.0);

    public static readonly StyledProperty<double> MaxValueProperty =
        AvaloniaProperty.Register<RadialGauge, double>(nameof(MaxValue), 15.0);

    public static readonly StyledProperty<double> WarningThresholdProperty =
        AvaloniaProperty.Register<RadialGauge, double>(nameof(WarningThreshold), 5.0);

    public static readonly StyledProperty<double> CriticalThresholdProperty =
        AvaloniaProperty.Register<RadialGauge, double>(nameof(CriticalThreshold), 10.0);

    public static readonly StyledProperty<bool> IsInvalidProperty =
        AvaloniaProperty.Register<RadialGauge, bool>(nameof(IsInvalid), false);

    public static readonly StyledProperty<bool> IsCriticalProperty =
        AvaloniaProperty.Register<RadialGauge, bool>(nameof(IsCritical), false);

    public static readonly StyledProperty<double?> PreviousValueProperty =
        AvaloniaProperty.Register<RadialGauge, double?>(nameof(PreviousValue), null);

    static RadialGauge()
    {
        AffectsRender<RadialGauge>(
            ValueProperty, MinValueProperty, MaxValueProperty,
            WarningThresholdProperty, CriticalThresholdProperty,
            IsInvalidProperty, IsCriticalProperty, PreviousValueProperty);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double MinValue
    {
        get => GetValue(MinValueProperty);
        set => SetValue(MinValueProperty, value);
    }

    public double MaxValue
    {
        get => GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
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

    public bool IsInvalid
    {
        get => GetValue(IsInvalidProperty);
        set => SetValue(IsInvalidProperty, value);
    }

    public bool IsCritical
    {
        get => GetValue(IsCriticalProperty);
        set => SetValue(IsCriticalProperty, value);
    }

    public double? PreviousValue
    {
        get => GetValue(PreviousValueProperty);
        set => SetValue(PreviousValueProperty, value);
    }

    // Arc goes from 225° to -45° (270° sweep), with 0° at right = 3 o'clock
    // Start: 225° (bottom-left), End: -45° = 315° (bottom-right)
    private const double StartAngleDeg = 225.0;
    private const double SweepAngleDeg = 270.0;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var w = Bounds.Width;
        var h = Bounds.Height;
        var cx = w / 2;
        var cy = h / 2 + h * 0.05;
        var radius = Math.Min(w, h) * 0.42;
        var trackThickness = radius * 0.18;

        // Background tile
        var bgColor = IsInvalid ? Color.FromRgb(0x1a, 0x1e, 0x24) :
                      IsCritical ? Color.FromRgb(0x2a, 0x10, 0x10) :
                                   Color.FromRgb(0x16, 0x1b, 0x22);
        context.FillRectangle(new SolidColorBrush(bgColor), Bounds);

        if (IsInvalid)
        {
            DrawArcTrack(context, cx, cy, radius, trackThickness, 0, SweepAngleDeg, Color.FromRgb(0x30, 0x36, 0x3d));
            DrawCenteredText(context, "INVALID", cx, cy - 10, 13, "#768390");
            DrawCenteredText(context, "overflow", cx, cy + 8, 11, "#4a5260");
            return;
        }

        var min = MinValue;
        var max = MaxValue;
        var warn = Math.Abs(WarningThreshold);
        var crit = Math.Abs(CriticalThreshold);

        // Zone arcs (symmetric: green center, amber edges, red extremes)
        var totalRange = max - min;
        var warnFrac = warn / (max - min) * 2; // fraction from center
        var critFrac = crit / (max - min) * 2;

        // Draw zones: green inner, amber middle, red outer
        var greenSweep = SweepAngleDeg * critFrac;
        var amberSweep = SweepAngleDeg * (critFrac - warnFrac) * 0.5;
        // Simplified: draw full track grey, overlay zones
        DrawArcTrack(context, cx, cy, radius, trackThickness, 0, SweepAngleDeg, Color.FromRgb(0x30, 0x36, 0x3d));

        // Green zone: center portion (safe range)
        var warnOffset = (1.0 - warnFrac) * SweepAngleDeg / 2.0;
        var warnSweep = SweepAngleDeg * warnFrac;
        DrawArcTrack(context, cx, cy, radius, trackThickness, warnOffset, warnSweep, Color.FromRgb(0x1a, 0x52, 0x2a));

        // Amber zone: beyond warning, before critical
        if (crit > warn)
        {
            var ambOffset = (1.0 - critFrac) * SweepAngleDeg / 2.0;
            var ambSweep = (critFrac - warnFrac) / 2.0 * SweepAngleDeg;
            DrawArcTrack(context, cx, cy, radius, trackThickness, ambOffset, ambSweep, Color.FromRgb(0x5a, 0x40, 0x0a));
            DrawArcTrack(context, cx, cy, radius, trackThickness,
                StartAngleDeg - ambOffset - amberSweep + StartAngleDeg + warnOffset + warnSweep - StartAngleDeg,
                ambSweep, Color.FromRgb(0x5a, 0x40, 0x0a));

            // Red zone: beyond critical
            DrawArcTrack(context, cx, cy, radius, trackThickness, 0, ambOffset, Color.FromRgb(0x5a, 0x18, 0x16));
            DrawArcTrack(context, cx, cy, radius, trackThickness, SweepAngleDeg - ambOffset, ambOffset, Color.FromRgb(0x5a, 0x18, 0x16));
        }

        // Needle
        var valueClamped = Math.Clamp(Value, min, max);
        var valueFrac = (valueClamped - min) / (max - min);
        var needleAngleDeg = StartAngleDeg + valueFrac * SweepAngleDeg;
        var needleAngleRad = needleAngleDeg * Math.PI / 180.0;
        var needleLen = radius - trackThickness / 2 - 4;
        var nx = cx + Math.Cos(needleAngleRad) * needleLen;
        var ny = cy + Math.Sin(needleAngleRad) * needleLen;

        var needleColor = IsCritical ? Color.FromRgb(0xf8, 0x51, 0x49) :
                          Math.Abs(Value) >= CriticalThreshold ? Color.FromRgb(0xf8, 0x51, 0x49) :
                          Math.Abs(Value) >= WarningThreshold ? Color.FromRgb(0xd2, 0x99, 0x22) :
                          Color.FromRgb(0xe6, 0xed, 0xf3);

        context.DrawLine(new Pen(new SolidColorBrush(needleColor), 2.5), new Point(cx, cy), new Point(nx, ny));
        context.FillRectangle(new SolidColorBrush(needleColor), new Rect(cx - 3, cy - 3, 6, 6));

        // Center value text
        var valueStr = Value.ToString("F2") + "°";
        DrawCenteredText(context, valueStr, cx, cy - radius * 0.30, 20, needleColor == Color.FromRgb(0xe6, 0xed, 0xf3) ? "#e6edf3" : ColorToHex(needleColor));

        // Delta
        if (PreviousValue is { } prev)
        {
            var delta = Value - prev;
            var deltaStr = delta >= 0 ? $"+{delta:F2}°" : $"{delta:F2}°";
            DrawCenteredText(context, deltaStr, cx, cy + radius * 0.05, 11, "#8b949e");
        }

        // Scale labels
        DrawScaleLabel(context, min.ToString("F0") + "°", StartAngleDeg, cx, cy, radius + trackThickness / 2 + 10);
        DrawScaleLabel(context, max.ToString("F0") + "°", StartAngleDeg + SweepAngleDeg, cx, cy, radius + trackThickness / 2 + 10);
        DrawScaleLabel(context, "0°", StartAngleDeg + SweepAngleDeg / 2, cx, cy, radius + trackThickness / 2 + 10);
    }

    private static void DrawArcTrack(DrawingContext context, double cx, double cy, double radius, double thickness,
        double offsetDeg, double sweepDeg, Color color)
    {
        if (sweepDeg <= 0)
        {
            return;
        }

        var outerR = radius;
        var innerR = radius - thickness;
        var pen = new Pen(new SolidColorBrush(color), thickness * 0.85) { LineCap = PenLineCap.Flat };
        var midR = (outerR + innerR) / 2.0;

        var startDeg = StartAngleDeg + offsetDeg;
        var endDeg = startDeg + sweepDeg;
        const int steps = 64;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var i = 0; i <= steps; i++)
            {
                var angleDeg = startDeg + (endDeg - startDeg) * i / steps;
                var angleRad = angleDeg * Math.PI / 180.0;
                var pt = new Point(cx + Math.Cos(angleRad) * midR, cy + Math.Sin(angleRad) * midR);
                if (i == 0)
                {
                    g.BeginFigure(pt, false);
                }
                else
                {
                    g.LineTo(pt);
                }
            }
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawCenteredText(DrawingContext context, string text, double cx, double cy, double size, string color)
    {
        var ft = new FormattedText(
            text,
            Thread.CurrentThread.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("JetBrains Mono, Cascadia Code, Consolas, monospace"),
            size,
            new SolidColorBrush(Color.Parse(color)));
        context.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
    }

    private static void DrawScaleLabel(DrawingContext context, string text, double angleDeg, double cx, double cy, double r)
    {
        var rad = angleDeg * Math.PI / 180.0;
        var x = cx + Math.Cos(rad) * r;
        var y = cy + Math.Sin(rad) * r;
        var ft = new FormattedText(
            text,
            Thread.CurrentThread.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            9,
            new SolidColorBrush(Color.Parse("#8b949e")));
        context.DrawText(ft, new Point(x - ft.Width / 2, y - ft.Height / 2));
    }

    private static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
