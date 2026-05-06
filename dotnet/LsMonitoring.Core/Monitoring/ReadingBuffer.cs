using LsMonitoring.Core.Models;
using LsMonitoring.Core.Parsing;

namespace LsMonitoring.Core.Monitoring;

public sealed class ReadingBuffer
{
    private readonly List<Reading> _readings = [];

    public IReadOnlyList<Reading> Readings => _readings;
    public Reading? Latest => _readings.Count == 0 ? null : _readings[^1];
    public int Count => _readings.Count;
    public double? EstimatedSamplingIntervalSeconds => CmtCsvParser.EstimateSamplingIntervalSeconds(_readings);

    public IReadOnlyList<Reading> Merge(IEnumerable<Reading> incoming, int maxPoints)
    {
        var known = _readings.Select(x => x.Timestamp).ToHashSet();
        var added = new List<Reading>();

        foreach (var reading in incoming)
        {
            if (!known.Add(reading.Timestamp))
            {
                continue;
            }

            _readings.Add(reading);
            added.Add(reading);
        }

        if (added.Count == 0)
        {
            return [];
        }

        _readings.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        Trim(maxPoints);
        return added;
    }

    public void Replace(IEnumerable<Reading> readings, int maxPoints)
    {
        _readings.Clear();
        _readings.AddRange(readings.OrderBy(x => x.Timestamp));
        Trim(maxPoints);
    }

    private void Trim(int maxPoints)
    {
        if (maxPoints <= 0)
        {
            return;
        }

        if (_readings.Count > maxPoints)
        {
            _readings.RemoveRange(0, _readings.Count - maxPoints);
        }
    }
}
