using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LsMonitoring.Avalonia.Controls;

public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> PointsProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Points));

    public static readonly StyledProperty<string> LineColorProperty =
        AvaloniaProperty.Register<Sparkline, string>(nameof(LineColor), "#2f81f7");

    static Sparkline()
    {
        AffectsRender<Sparkline>(PointsProperty, LineColorProperty);
    }

    public IReadOnlyList<double>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public string LineColor
    {
        get => GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var points = Points;
        if (points is null || points.Count < 2)
        {
            return;
        }

        var w = Bounds.Width;
        var h = Bounds.Height;

        var min = points.Min();
        var max = points.Max();
        var range = max - min;
        if (range < 0.001)
        {
            range = 1.0;
        }

        var brush = new SolidColorBrush(Color.Parse(LineColor));
        var pen = new Pen(brush, 1.5);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var i = 0; i < points.Count; i++)
            {
                var x = w * i / (points.Count - 1);
                var y = h - (points[i] - min) / range * h;
                var pt = new Point(x, Math.Clamp(y, 0, h));
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
}
