namespace LsMonitoring.Core.Alarms;

public enum AlertStateChangeKind
{
    Started,
    Active,
    Resolved
}

public sealed class AlertStateChange
{
    public required AlertStateChangeKind Kind { get; init; }
    public required AlertEvent Event { get; init; }
}

public sealed class AlertStateTracker
{
    private readonly Dictionary<(int NodeId, string Axis), ActiveAlertState> _active = [];

    public AlertStateChange? Update(
        int nodeId,
        string axis,
        bool isCritical,
        double value,
        double threshold,
        DateTime timestamp)
    {
        var normalizedAxis = axis.Trim().ToUpperInvariant();
        var key = (nodeId, normalizedAxis);

        if (isCritical)
        {
            if (_active.TryGetValue(key, out var active))
            {
                active.CurrentValue = value;
                active.UpdatedAt = timestamp;
                active.Threshold = threshold;
                active.PeakAbsValue = Math.Max(active.PeakAbsValue, Math.Abs(value));
                return new AlertStateChange
                {
                    Kind = AlertStateChangeKind.Active,
                    Event = active.ToEvent()
                };
            }

            active = new ActiveAlertState
            {
                NodeId = nodeId,
                Axis = normalizedAxis,
                StartedAt = timestamp,
                UpdatedAt = timestamp,
                CurrentValue = value,
                PeakAbsValue = Math.Abs(value),
                Threshold = threshold
            };
            _active[key] = active;
            return new AlertStateChange
            {
                Kind = AlertStateChangeKind.Started,
                Event = active.ToEvent()
            };
        }

        if (!_active.Remove(key, out var resolved))
        {
            return null;
        }

        resolved.CurrentValue = value;
        resolved.UpdatedAt = timestamp;
        resolved.ResolvedAt = timestamp;
        return new AlertStateChange
        {
            Kind = AlertStateChangeKind.Resolved,
            Event = resolved.ToEvent()
        };
    }

    private sealed class ActiveAlertState
    {
        public required int NodeId { get; init; }
        public required string Axis { get; init; }
        public required DateTime StartedAt { get; init; }
        public required DateTime UpdatedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public required double CurrentValue { get; set; }
        public required double PeakAbsValue { get; set; }
        public required double Threshold { get; set; }

        public AlertEvent ToEvent()
        {
            return new AlertEvent
            {
                NodeId = NodeId,
                Axis = Axis,
                StartedAt = StartedAt,
                UpdatedAt = UpdatedAt,
                ResolvedAt = ResolvedAt,
                CurrentValue = CurrentValue,
                PeakAbsValue = PeakAbsValue,
                Threshold = Threshold
            };
        }
    }
}
