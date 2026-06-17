using LsMonitoring.Core.Configuration;
using LsMonitoring.Core.Models;

namespace LsMonitoring.Core.Alarms;

public sealed record AlertReadingResult(
    Evaluation Evaluation,
    IReadOnlyList<AlertStateChange> Changes);

public static class AlertReadingProcessor
{
    public static AlertReadingResult UpdateState(
        AlertStateTracker tracker,
        int nodeId,
        Reading reading,
        Thresholds thresholds,
        NodeCalibration? calibration = null)
    {
        var evaluation = ThresholdEvaluator.EvaluateAxisThresholds(reading, thresholds, calibration);
        var changes = new List<AlertStateChange>(capacity: 2);

        AddChange(changes, tracker.Update(
            nodeId,
            "A",
            IsCritical(evaluation.AValue, thresholds.CriticalA),
            evaluation.AValue ?? 0,
            thresholds.CriticalA,
            reading.Timestamp));

        var criticalB = thresholds.EffectiveCriticalB();
        AddChange(changes, tracker.Update(
            nodeId,
            "B",
            IsCritical(evaluation.BValue, criticalB),
            evaluation.BValue ?? 0,
            criticalB,
            reading.Timestamp));

        return new AlertReadingResult(evaluation, changes);
    }

    private static bool IsCritical(double? value, double threshold)
    {
        return value is { } actual && Math.Abs(actual) >= threshold;
    }

    private static void AddChange(List<AlertStateChange> changes, AlertStateChange? change)
    {
        if (change is not null)
        {
            changes.Add(change);
        }
    }
}
