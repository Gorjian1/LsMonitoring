using System.ComponentModel;
using System.Runtime.CompilerServices;
using LsMonitoring.Core.Models;
using LsMonitoring.Core.Monitoring;

namespace LsMonitoring.Avalonia;

public sealed class NodeListItem : INotifyPropertyChanged
{
    private int _pointCount;
    private DateTime? _latestTimestamp;
    private string? _model;
    private string _connectionText = "No data";
    private string _aText = "-";
    private string _bText = "-";
    private string _tText = "-";
    private string _ageText = "No data";
    private string _statusColor = "#768390";
    private IReadOnlyList<double> _sparklineData = [];
    private bool _isStale;

    public NodeListItem(int nodeId)
    {
        NodeId = nodeId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int NodeId { get; }
    public string Title => $"Node {NodeId}";

    public string Subtitle => string.IsNullOrWhiteSpace(Model)
        ? $"{PointCount} pts"
        : $"{Model}  —  {PointCount} pts";

    public string? Model
    {
        get => _model;
        set
        {
            if (SetField(ref _model, value))
            {
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public int PointCount
    {
        get => _pointCount;
        set
        {
            if (SetField(ref _pointCount, value))
            {
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public DateTime? LatestTimestamp
    {
        get => _latestTimestamp;
        set => SetField(ref _latestTimestamp, value);
    }

    public string ConnectionText
    {
        get => _connectionText;
        set => SetField(ref _connectionText, value);
    }

    public string AText
    {
        get => _aText;
        set => SetField(ref _aText, value);
    }

    public string BText
    {
        get => _bText;
        set => SetField(ref _bText, value);
    }

    public string TText
    {
        get => _tText;
        set => SetField(ref _tText, value);
    }

    public string AgeText
    {
        get => _ageText;
        set => SetField(ref _ageText, value);
    }

    public string StatusColor
    {
        get => _statusColor;
        set => SetField(ref _statusColor, value);
    }

    public IReadOnlyList<double> SparklineData
    {
        get => _sparklineData;
        set => SetField(ref _sparklineData, value);
    }

    public bool IsStale
    {
        get => _isStale;
        set => SetField(ref _isStale, value);
    }

    public void UpdateFrom(ReadingBuffer buffer, string? model = null)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            Model = model;
        }

        PointCount = buffer.Count;
        var latest = buffer.Latest;
        LatestTimestamp = latest?.Timestamp;
        ConnectionText = ReadingSnapshot.LinkText(buffer, DateTime.Now);
        AText = latest is null ? "-" : ReadingSnapshot.FormatValue(latest.AAxis);
        BText = latest is null ? "-" : ReadingSnapshot.FormatValue(latest.BAxis);
        TText = latest is null ? "-" : ReadingSnapshot.FormatValue(latest.Temperature, digits: 1);
        AgeText = FormatAge(latest?.Timestamp, DateTime.Now);
        IsStale = ReadingSnapshot.IsStale(buffer, DateTime.Now);
        StatusColor = latest is null ? "#768390" :
                      IsStale ? "#768390" :
                      latest.Invalid ? "#f85149" :
                      "#238636";

        SparklineData = buffer.Readings
            .TakeLast(50)
            .Where(r => r.AAxis.HasValue && !r.Invalid)
            .Select(r => r.AAxis!.Value)
            .ToList();
    }

    private static string FormatAge(DateTime? timestamp, DateTime now)
    {
        if (timestamp is not { } ts)
        {
            return "No data";
        }

        var age = now - ts;
        if (age.TotalSeconds < 60)
        {
            return $"{(int)age.TotalSeconds}s ago";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        return $"{(int)age.TotalHours}h ago";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
