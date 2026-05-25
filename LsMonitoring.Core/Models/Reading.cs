using System.Globalization;

namespace LsMonitoring.Core.Models;

public sealed class Reading
{
    public const double SensorRangeDegrees = 15.0;

    public DateTime Timestamp { get; init; }
    public double? Temperature { get; init; }
    public double? AAxis { get; init; }
    public double? BAxis { get; init; }
    public double? AVariation { get; init; }
    public double? BVariation { get; init; }
    public bool Invalid { get; private set; }
    public List<string> InvalidReasons { get; } = [];

    public void MarkInvalidIfOutOfRange()
    {
        CheckAxis("A", AAxis);
        CheckAxis("B", BAxis);
    }

    public string ToRowText()
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{Timestamp:yyyy-MM-dd HH:mm:ss}   T={Format(Temperature),6}   A={Format(AAxis),8}   B={Format(BAxis),8}");
    }

    private void CheckAxis(string name, double? value)
    {
        if (value is not { } v || Math.Abs(v) <= SensorRangeDegrees)
        {
            return;
        }

        Invalid = true;
        InvalidReasons.Add(string.Create(CultureInfo.InvariantCulture,
            $"{name}={v:F4} out of +/-{SensorRangeDegrees} deg"));
    }

    private static string Format(double? value)
    {
        return value is { } v ? v.ToString("F4", CultureInfo.InvariantCulture) : "";
    }
}
