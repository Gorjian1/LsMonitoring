using LsMonitoring.Core.Models;
using LsMonitoring.Core.Monitoring;
using LsMonitoring.Core.Parsing;
using LsMonitoring.Core.Alarms;
using LsMonitoring.Core.Configuration;
using LsMonitoring.Core.Export;

namespace LsMonitoring.Core.Tests;

public sealed class CmtCsvParserTests
{
    private static string SampleCsvPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "fixtures", "sample_readings.csv"));

    [Fact(Skip = "Requires fixtures/sample_readings.csv, which is not committed because gateway exports can contain private sensor data.")]
    public void ParsesRealGatewayCsv()
    {
        var parsed = CmtCsvParser.ParseFile(SampleCsvPath);

        Assert.Equal(6989, parsed.NodeId);
        Assert.Equal("LS-G6-INC15", parsed.Model);
        Assert.Equal("Ch1", parsed.Channel);
        Assert.Equal(90, parsed.Readings.Count);

        var first = parsed.Readings[0];
        Assert.Equal(new DateTime(2026, 5, 5, 10, 2, 30), first.Timestamp);
        Assert.Equal(26.8, first.Temperature);
        Assert.Equal(-2.8377, first.AAxis);
        Assert.Equal(-0.2681, first.BAxis);
        Assert.Null(first.AVariation);
        Assert.Null(first.BVariation);
        Assert.False(first.Invalid);
    }

    [Fact(Skip = "Requires fixtures/sample_readings.csv, which is not committed because gateway exports can contain private sensor data.")]
    public void MarksOutOfRangeAxesInvalidButKeepsRawValues()
    {
        var parsed = CmtCsvParser.ParseFile(SampleCsvPath);
        var invalid = parsed.Readings.Where(x => x.Invalid).ToList();

        Assert.True(invalid.Count > 10);
        Assert.Contains(invalid, x => x.AAxis is { } a && Math.Abs(a) > Reading.SensorRangeDegrees);
        Assert.All(invalid, x =>
        {
            Assert.True(
                x.AAxis is { } a && Math.Abs(a) > Reading.SensorRangeDegrees ||
                x.BAxis is { } b && Math.Abs(b) > Reading.SensorRangeDegrees);
        });
    }

    [Fact(Skip = "Requires fixtures/sample_readings.csv, which is not committed because gateway exports can contain private sensor data.")]
    public void EstimatesSamplingInterval()
    {
        var parsed = CmtCsvParser.ParseFile(SampleCsvPath);

        Assert.Equal(30.0, CmtCsvParser.EstimateSamplingIntervalSeconds(parsed.Readings));
    }

    [Fact(Skip = "Requires fixtures/sample_readings.csv, which is not committed because gateway exports can contain private sensor data.")]
    public void ReadingBufferMergesByTimestampAndTrims()
    {
        var parsed = CmtCsvParser.ParseFile(SampleCsvPath);
        var buffer = new ReadingBuffer();

        Assert.Equal(90, buffer.Merge(parsed.Readings, maxPoints: 1000).Count);
        Assert.Empty(buffer.Merge(parsed.Readings, maxPoints: 1000));

        buffer.Replace(parsed.Readings, maxPoints: 10);
        Assert.Equal(10, buffer.Count);
        Assert.Equal(parsed.Readings[^1].Timestamp, buffer.Latest?.Timestamp);
    }

    [Fact(Skip = "Requires fixtures/sample_readings.csv, which is not committed because gateway exports can contain private sensor data.")]
    public void EvaluatesThresholdsWhileKeepingInvalidRawValues()
    {
        var parsed = CmtCsvParser.ParseFile(SampleCsvPath);
        var thresholds = new Thresholds
        {
            WarningA = 2.5,
            CriticalA = 5.0,
            SameForAb = true,
            Mode = "absolute"
        };

        var statuses = parsed.Readings
            .Select(x => ThresholdEvaluator.Evaluate(x, thresholds, new AlarmConfig()).Status)
            .ToHashSet();

        Assert.Contains(Status.Ok, statuses);
        Assert.Contains(Status.Warning, statuses);
        Assert.Contains(Status.Critical, statuses);
        Assert.Contains(parsed.Readings, x =>
            x.Invalid && ThresholdEvaluator.Evaluate(x, thresholds, new AlarmConfig()).Status == Status.Critical);
    }

    [Fact]
    public void EvaluatesDeviationFromConfiguredZeroInBothModes()
    {
        var reading = new Reading
        {
            Timestamp = DateTime.UtcNow,
            AAxis = 12.5,
            BAxis = -7.0,
            AVariation = 0.25,
            BVariation = -0.40
        };

        var absoluteThresholds = new Thresholds
        {
            Mode = Thresholds.AbsoluteMode,
            ZeroA = 10.0,
            ZeroB = -5.0,
            WarningA = 1.0,
            WarningB = 1.0,
            CriticalA = 3.0,
            CriticalB = 3.0,
            SameForAb = false
        };

        var absolute = ThresholdEvaluator.EvaluateAxisThresholds(reading, absoluteThresholds);
        Assert.Equal(Status.Warning, absolute.Status);
        Assert.Equal(2.5, absolute.AValue);
        Assert.Equal(-2.0, absolute.BValue);

        var variationThresholds = new Thresholds
        {
            Mode = Thresholds.VariationMode,
            ZeroA = 0.10,
            ZeroB = -0.10,
            WarningA = 0.1,
            WarningB = 0.1,
            CriticalA = 0.2,
            CriticalB = 0.2,
            SameForAb = false
        };

        var variation = ThresholdEvaluator.EvaluateAxisThresholds(reading, variationThresholds);
        Assert.Equal(Status.Critical, variation.Status);
        Assert.NotNull(variation.AValue);
        Assert.NotNull(variation.BValue);
        Assert.Equal(0.15, variation.AValue.Value, precision: 10);
        Assert.Equal(-0.30, variation.BValue.Value, precision: 10);
    }

    [Fact(Skip = "Requires fixtures/sample_readings.csv, which is not committed because gateway exports can contain private sensor data.")]
    public void ExportsExcelCompatibleCsv()
    {
        var parsed = CmtCsvParser.ParseFile(SampleCsvPath);
        var path = Path.Combine(Path.GetTempPath(), $"ls-monitoring-{Guid.NewGuid():N}.csv");

        try
        {
            ReadingsCsvExporter.Export(path, parsed.Readings.Take(3), new Thresholds(), new AlarmConfig());
            var text = File.ReadAllText(path);

            Assert.Contains("Date-and-time,Temperature,A-axis,B-axis", text);
            Assert.Contains("2026-05-05 10:02:30", text);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
