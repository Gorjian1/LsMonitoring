using LsMonitoring.Core.Configuration;
using LsMonitoring.Core.Models;
using LsMonitoring.Core.Monitoring;

namespace LsMonitoring.Avalonia;

public sealed class ReadingRow
{
    public ReadingRow(Reading reading, Thresholds? thresholds = null, NodeCalibration? calibration = null)
    {
        Time = ReadingSnapshot.FormatTimestamp(reading.Timestamp);
        Temperature = ReadingSnapshot.FormatValue(reading.Temperature, digits: 1);
        AAxis = ReadingSnapshot.FormatValue(reading.AAxis);
        BAxis = ReadingSnapshot.FormatValue(reading.BAxis);
        AVariation = ReadingSnapshot.FormatValue(reading.AVariation);
        BVariation = ReadingSnapshot.FormatValue(reading.BVariation);
        ADeviation = thresholds is null ? "-" : ReadingSnapshot.FormatValue(thresholds.DeviationA(reading, calibration?.ZeroA));
        BDeviation = thresholds is null ? "-" : ReadingSnapshot.FormatValue(thresholds.DeviationB(reading, calibration?.ZeroB));
        Flags = ReadingSnapshot.Flags(reading);
    }

    public string Time { get; }
    public string Temperature { get; }
    public string AAxis { get; }
    public string BAxis { get; }
    public string AVariation { get; }
    public string BVariation { get; }
    public string ADeviation { get; }
    public string BDeviation { get; }
    public string Flags { get; }
}
