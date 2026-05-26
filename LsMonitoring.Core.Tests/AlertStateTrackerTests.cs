using LsMonitoring.Core.Alarms;

namespace LsMonitoring.Core.Tests;

public sealed class AlertStateTrackerTests
{
    [Fact]
    public void Update_EmitsStartedActiveResolvedOncePerAxis()
    {
        var tracker = new AlertStateTracker();
        var start = new DateTime(2026, 5, 26, 10, 0, 0);

        var started = tracker.Update(6989, "A", true, -12.5, 10, start);
        var active = tracker.Update(6989, "A", true, -14.0, 10, start.AddSeconds(30));
        var resolved = tracker.Update(6989, "A", false, -1.0, 10, start.AddMinutes(2));
        var duplicateResolved = tracker.Update(6989, "A", false, -0.5, 10, start.AddMinutes(3));

        Assert.NotNull(started);
        Assert.Equal(AlertStateChangeKind.Started, started.Kind);
        Assert.Equal(start, started.Event.StartedAt);

        Assert.NotNull(active);
        Assert.Equal(AlertStateChangeKind.Active, active.Kind);
        Assert.Equal(14.0, active.Event.PeakAbsValue);

        Assert.NotNull(resolved);
        Assert.Equal(AlertStateChangeKind.Resolved, resolved.Kind);
        Assert.Equal(TimeSpan.FromMinutes(2), resolved.Event.Duration);

        Assert.Null(duplicateResolved);
    }

    [Fact]
    public void Update_TracksAxesIndependently()
    {
        var tracker = new AlertStateTracker();
        var timestamp = new DateTime(2026, 5, 26, 10, 0, 0);

        var a = tracker.Update(6989, "A", true, 11, 10, timestamp);
        var b = tracker.Update(6989, "B", true, 12, 10, timestamp);

        Assert.Equal(AlertStateChangeKind.Started, a?.Kind);
        Assert.Equal(AlertStateChangeKind.Started, b?.Kind);
        Assert.Equal("A", a?.Event.Axis);
        Assert.Equal("B", b?.Event.Axis);
    }
}
