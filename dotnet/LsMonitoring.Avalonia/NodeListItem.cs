using System.ComponentModel;
using System.Runtime.CompilerServices;
using LsMonitoring.Core.Configuration;
using LsMonitoring.Core.Models;
using LsMonitoring.Core.Monitoring;

namespace LsMonitoring.Avalonia;

public sealed class NodeListItem : INotifyPropertyChanged
{
    private int _pointCount;
    private DateTime? _latestTimestamp;
    private string? _model;
    private string _connectionText = "Нет данных";
    private string _aText = "-";
    private string _bText = "-";
    private string _tText = "-";
    private string _ageText = "Нет данных";
    private string _statusColor = "#8c959f";
    private IReadOnlyList<double> _sparklineData = [];
    private bool _isStale;
    private bool _hasZero;
    private string _zeroText = "";

    public NodeListItem(int nodeId)
    {
        NodeId = nodeId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int NodeId { get; }
    public string Title => $"Узел {NodeId}";

    public string Subtitle => string.IsNullOrWhiteSpace(Model)
        ? $"{PointCount} точ."
        : $"{Model}  —  {PointCount} точ.";

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

    public bool HasZero
    {
        get => _hasZero;
        set => SetField(ref _hasZero, value);
    }

    public string ZeroText
    {
        get => _zeroText;
        set => SetField(ref _zeroText, value);
    }

    public void UpdateFrom(ReadingBuffer buffer, string? model = null, NodeCalibration? calibration = null)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            Model = model;
        }

        PointCount = buffer.Count;
        var latest = buffer.Latest;
        LatestTimestamp = latest?.Timestamp;
        ConnectionText = ReadingSnapshot.LinkText(buffer, DateTime.Now);

        var aRaw = latest?.AAxis;
        var bRaw = latest?.BAxis;
        var aShown = aRaw is { } av && calibration?.ZeroA is { } za ? av - za : aRaw;
        var bShown = bRaw is { } bv && calibration?.ZeroB is { } zb ? bv - zb : bRaw;

        AText = latest is null ? "-" : ReadingSnapshot.FormatValue(aShown);
        BText = latest is null ? "-" : ReadingSnapshot.FormatValue(bShown);
        TText = latest is null ? "-" : ReadingSnapshot.FormatValue(latest.Temperature, digits: 1);
        AgeText = FormatAge(latest?.Timestamp, DateTime.Now);
        IsStale = ReadingSnapshot.IsStale(buffer, DateTime.Now);
        StatusColor = latest is null ? "#8c959f" :
                      IsStale ? "#8c959f" :
                      latest.Invalid ? "#cf222e" :
                      "#1a7f37";

        HasZero = calibration?.HasZero == true;
        ZeroText = calibration?.HasZero == true
            ? $"0: A={ReadingSnapshot.FormatValue(calibration.ZeroA)}  B={ReadingSnapshot.FormatValue(calibration.ZeroB)}"
            : "";

        SparklineData = buffer.Readings
            .TakeLast(50)
            .Where(r => r.AAxis.HasValue && !r.Invalid)
            .Select(r => calibration?.ZeroA is { } z ? r.AAxis!.Value - z : r.AAxis!.Value)
            .ToList();
    }

    private static string FormatAge(DateTime? timestamp, DateTime now)
    {
        if (timestamp is not { } ts)
        {
            return "нет данных";
        }

        var age = now - ts;
        if (age.TotalSeconds < 60)
        {
            return $"{(int)age.TotalSeconds} с назад";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{(int)age.TotalMinutes} мин назад";
        }

        return $"{(int)age.TotalHours} ч назад";
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
