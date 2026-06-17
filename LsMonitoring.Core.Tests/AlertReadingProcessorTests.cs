using LsMonitoring.Core.Alarms;
using LsMonitoring.Core.Configuration;
using LsMonitoring.Core.Models;

namespace LsMonitoring.Core.Tests;

public sealed class AlertReadingProcessorTests
{
    [Fact]
    public void UpdateState_EmitsStartedForBothAxesWhenBothAreCritical()
    {
        var tracker = new AlertStateTracker();
        var reading = new Reading
        {
            Timestamp = new DateTime(2026, 6, 17, 10, 0, 0),
            AAxis = 1.25,
            BAxis = -2.50
        };
        var thresholds = new Thresholds
        {
            CriticalA = 1.0,
            CriticalB = 2.0,
            SameForAb = false
        };

        var result = AlertReadingProcessor.UpdateState(tracker, 6989, reading, thresholds);

        Assert.Equal(Status.Critical, result.Evaluation.Status);
        Assert.Equal(2, result.Changes.Count);
        Assert.All(result.Changes, change => Assert.Equal(AlertStateChangeKind.Started, change.Kind));
        Assert.Contains(result.Changes, change => change.Event.Axis == "A" && change.Event.CurrentValue == 1.25);
        Assert.Contains(result.Changes, change => change.Event.Axis == "B" && change.Event.CurrentValue == -2.50);
    }

    [Fact]
    public void UpdateState_EmitsBOnlyWhenOnlyBExceedsSeparateThreshold()
    {
        var tracker = new AlertStateTracker();
        var reading = new Reading
        {
            Timestamp = new DateTime(2026, 6, 17, 10, 0, 0),
            AAxis = 0.25,
            BAxis = 2.10
        };
        var thresholds = new Thresholds
        {
            CriticalA = 1.0,
            CriticalB = 2.0,
            SameForAb = false
        };

        var result = AlertReadingProcessor.UpdateState(tracker, 6989, reading, thresholds);

        var change = Assert.Single(result.Changes);
        Assert.Equal(AlertStateChangeKind.Started, change.Kind);
        Assert.Equal("B", change.Event.Axis);
        Assert.Equal(2.10, change.Event.CurrentValue);
    }

    [Fact]
    public void UpdateState_UsesEffectiveBThresholdWhenThresholdsAreShared()
    {
        var tracker = new AlertStateTracker();
        var reading = new Reading
        {
            Timestamp = new DateTime(2026, 6, 17, 10, 0, 0),
            AAxis = 0.25,
            BAxis = 3.10
        };
        var thresholds = new Thresholds
        {
            CriticalA = 3.0,
            CriticalB = 99.0,
            SameForAb = true
        };

        var result = AlertReadingProcessor.UpdateState(tracker, 6989, reading, thresholds);

        var change = Assert.Single(result.Changes);
        Assert.Equal("B", change.Event.Axis);
        Assert.Equal(3.0, change.Event.Threshold);
    }
}
