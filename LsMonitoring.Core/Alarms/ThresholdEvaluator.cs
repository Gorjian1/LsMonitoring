using System.Globalization;
using LsMonitoring.Core.Configuration;
using LsMonitoring.Core.Models;

namespace LsMonitoring.Core.Alarms;

public static class ThresholdEvaluator
{
    public static Evaluation Evaluate(Reading reading, Thresholds thresholds, NodeCalibration? calibration = null)
    {
        var evaluation = EvaluateAxisThresholds(reading, thresholds, calibration);
        if (!reading.Invalid)
        {
            return evaluation;
        }

        var reasons = reading.InvalidReasons.Concat(evaluation.Reasons).ToList();
        return new Evaluation
        {
            Status = evaluation.Status == Status.Ok ? Status.Invalid : evaluation.Status,
            Reasons = reasons,
            AValue = evaluation.AValue,
            BValue = evaluation.BValue
        };
    }

    public static Evaluation EvaluateAxisThresholds(Reading reading, Thresholds thresholds, NodeCalibration? calibration = null)
    {
        var aValue = thresholds.DeviationA(reading, calibration?.ZeroA);
        var bValue = thresholds.DeviationB(reading, calibration?.ZeroB);
        var warningA = thresholds.WarningA;
        var criticalA = thresholds.CriticalA;
        var warningB = thresholds.EffectiveWarningB();
        var criticalB = thresholds.EffectiveCriticalB();

        var status = Status.Ok;
        var reasons = new List<string>();

        if (aValue is { } a)
        {
            if (Math.Abs(a) >= criticalA)
            {
                status = Status.Critical;
                reasons.Add(string.Create(CultureInfo.InvariantCulture, $"|A-zero|={Math.Abs(a):F3} >= max {criticalA}"));
            }
            else if (Math.Abs(a) >= warningA)
            {
                status = Max(status, Status.Warning);
                reasons.Add(string.Create(CultureInfo.InvariantCulture, $"|A-zero|={Math.Abs(a):F3} >= warning {warningA}"));
            }
        }

        if (bValue is { } b)
        {
            if (Math.Abs(b) >= criticalB)
            {
                status = Status.Critical;
                reasons.Add(string.Create(CultureInfo.InvariantCulture, $"|B-zero|={Math.Abs(b):F3} >= max {criticalB}"));
            }
            else if (Math.Abs(b) >= warningB)
            {
                status = Max(status, Status.Warning);
                reasons.Add(string.Create(CultureInfo.InvariantCulture, $"|B-zero|={Math.Abs(b):F3} >= warning {warningB}"));
            }
        }

        return new Evaluation
        {
            Status = status,
            Reasons = reasons,
            AValue = aValue,
            BValue = bValue
        };
    }

    private static Status Max(Status left, Status right)
    {
        return Rank(left) >= Rank(right) ? left : right;
    }

    private static int Rank(Status status)
    {
        return status switch
        {
            Status.Critical => 2,
            Status.Warning => 1,
            _ => 0
        };
    }
}
