namespace LsMonitoring.Core.Alarms;

public sealed class Evaluation
{
    public required Status Status { get; init; }
    public required List<string> Reasons { get; init; }
    public double? AValue { get; init; }
    public double? BValue { get; init; }
}
