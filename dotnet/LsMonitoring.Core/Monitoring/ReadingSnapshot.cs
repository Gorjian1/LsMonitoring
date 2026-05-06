using System.Globalization;
using LsMonitoring.Core.Models;

namespace LsMonitoring.Core.Monitoring;

public static class ReadingSnapshot
{
    public static string FormatValue(double? value, int digits = 3)
    {
        return value is { } v ? v.ToString($"F{digits}", CultureInfo.InvariantCulture) : "-";
    }

    public static string FormatTimestamp(DateTime? timestamp)
    {
        return timestamp is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "-";
    }

    public static string Flags(Reading reading)
    {
        return reading.Invalid ? "RAW >15" : "";
    }

    public static bool IsStale(ReadingBuffer buffer, DateTime now)
    {
        var latest = buffer.Latest;
        if (latest is null)
        {
            return false;
        }

        var interval = buffer.EstimatedSamplingIntervalSeconds ?? 600.0;
        var staleAfter = TimeSpan.FromSeconds(Math.Max(interval * 3.0, TimeSpan.FromMinutes(30).TotalSeconds));
        return now - latest.Timestamp > staleAfter;
    }

    public static string LinkText(ReadingBuffer buffer, DateTime now)
    {
        var latest = buffer.Latest;
        if (latest is null)
        {
            return "No data";
        }

        var ts = FormatTimestamp(latest.Timestamp);
        return IsStale(buffer, now) ? $"{ts} (stale)" : ts;
    }
}
