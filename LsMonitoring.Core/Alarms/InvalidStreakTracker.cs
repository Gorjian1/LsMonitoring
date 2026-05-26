using LsMonitoring.Core.Configuration;
using LsMonitoring.Core.Models;

namespace LsMonitoring.Core.Alarms;

public sealed class InvalidStreakTracker
{
    private readonly Dictionary<int, DateTime> _streakStart = [];

    public Status? Update(int nodeId, Reading reading, AlarmConfig alarm)
    {
        if (!reading.Invalid)
        {
            _streakStart.Remove(nodeId);
            return null;
        }

        _streakStart.TryAdd(nodeId, reading.Timestamp);
        if (alarm.InvalidBehavior != "alarm")
        {
            return null;
        }

        var age = reading.Timestamp - _streakStart[nodeId];
        return age >= TimeSpan.FromMinutes(alarm.InvalidAlarmMinutes) ? Status.Critical : null;
    }
}
