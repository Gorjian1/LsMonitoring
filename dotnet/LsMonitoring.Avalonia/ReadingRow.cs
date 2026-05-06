using LsMonitoring.Core.Models;
using LsMonitoring.Core.Monitoring;

namespace LsMonitoring.Avalonia;

public sealed class ReadingRow
{
    public ReadingRow(Reading reading)
    {
        Time = ReadingSnapshot.FormatTimestamp(reading.Timestamp);
        Temperature = ReadingSnapshot.FormatValue(reading.Temperature, digits: 1);
        AAxis = ReadingSnapshot.FormatValue(reading.AAxis);
        BAxis = ReadingSnapshot.FormatValue(reading.BAxis);
        AVariation = ReadingSnapshot.FormatValue(reading.AVariation);
        BVariation = ReadingSnapshot.FormatValue(reading.BVariation);
        Flags = ReadingSnapshot.Flags(reading);
    }

    public string Time { get; }
    public string Temperature { get; }
    public string AAxis { get; }
    public string BAxis { get; }
    public string AVariation { get; }
    public string BVariation { get; }
    public string Flags { get; }
}
