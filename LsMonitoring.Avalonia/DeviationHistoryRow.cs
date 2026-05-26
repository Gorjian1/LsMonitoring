using System.ComponentModel;
using System.Runtime.CompilerServices;
using LsMonitoring.Core.Monitoring;

namespace LsMonitoring.Avalonia;

public sealed class DeviationHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int NodeId { get; set; }
    public string Axis { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public double StartValue { get; set; }
    public double LastValue { get; set; }
    public double PeakAbsValue { get; set; }
    public double Threshold { get; set; }
}

public sealed class DeviationHistoryRow : INotifyPropertyChanged
{
    public DeviationHistoryRow(DeviationHistoryEntry entry)
    {
        Entry = entry;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DeviationHistoryEntry Entry { get; }

    public string Header => $"Узел {Entry.NodeId} · ось {Entry.Axis}";

    public string StartedText => $"Старт: {ReadingSnapshot.FormatTimestamp(Entry.StartedAt)}";

    public string StatusText => Entry.EndedAt is null ? "Активно" : "Завершено";

    public string StatusBrush => Entry.EndedAt is null ? "#DC2626" : "#4F5865";

    public string Details
    {
        get
        {
            var ended = Entry.EndedAt is { } endedAt
                ? $" · конец {ReadingSnapshot.FormatTimestamp(endedAt)}"
                : "";
            return $"Длительность: {FormatDuration(Duration)}{ended} · откл. {Entry.LastValue:F3}° · пик {Entry.PeakAbsValue:F3}° · порог ±{Entry.Threshold:F2}°";
        }
    }

    private TimeSpan Duration => (Entry.EndedAt ?? DateTime.Now) - Entry.StartedAt;

    public void Refresh()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(Details));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours} ч {duration.Minutes:D2} мин";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.Minutes} мин {duration.Seconds:D2} с";
        }

        return $"{Math.Max(0, duration.Seconds)} с";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
