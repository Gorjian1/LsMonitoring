using System.Globalization;
using LsMonitoring.Core.Alarms;
using LsMonitoring.Core.Configuration;
using LsMonitoring.Core.Models;

namespace LsMonitoring.Core.Export;

public static class ReadingsCsvExporter
{
    private static readonly string[] Headers =
    [
        "Date-and-time",
        "Temperature",
        "A-axis",
        "B-axis",
        "A-variation",
        "B-variation",
        "A-deviation",
        "B-deviation",
        "Status",
        "Reasons"
    ];

    public static void Export(string path, IEnumerable<Reading> readings, Thresholds thresholds, AlarmConfig alarm, NodeCalibration? calibration = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var writer = new StreamWriter(path);
        writer.WriteLine(string.Join(",", Headers));

        foreach (var reading in readings)
        {
            var evaluation = ThresholdEvaluator.Evaluate(reading, thresholds, alarm, calibration);
            writer.WriteLine(string.Join(",",
                Escape(reading.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                Format(reading.Temperature),
                Format(reading.AAxis),
                Format(reading.BAxis),
                Format(reading.AVariation),
                Format(reading.BVariation),
                Format(evaluation.AValue),
                Format(evaluation.BValue),
                evaluation.Status,
                Escape(string.Join("; ", evaluation.Reasons))));
        }
    }

    private static string Format(double? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
