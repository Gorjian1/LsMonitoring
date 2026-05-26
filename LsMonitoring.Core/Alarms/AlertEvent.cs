namespace LsMonitoring.Core.Alarms;

public sealed class AlertEvent
{
    public required int NodeId { get; init; }
    public required string Axis { get; init; }
    public required DateTime StartedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public required double CurrentValue { get; init; }
    public required double PeakAbsValue { get; init; }
    public required double Threshold { get; init; }

    public TimeSpan Duration => (ResolvedAt ?? UpdatedAt) - StartedAt;
}

public interface IAlertChannel
{
    string ChannelName { get; }
    string? LastError { get; }
    Task<bool> SendTestAsync();
    Task NotifyStartedAsync(AlertEvent alertEvent);
    Task NotifyResolvedAsync(AlertEvent alertEvent);
}

public interface IUpdatableAlertChannel : IAlertChannel
{
    Task NotifyActiveAsync(AlertEvent alertEvent);
}
