using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LsMonitoring.Avalonia;

public partial class PlotLimitDialog : Window
{
    private static readonly string[] DateFormats =
    [
        "dd.MM.yyyy",
        "d.M.yyyy",
        "yyyy-MM-dd"
    ];

    private static readonly string[] TimeFormats =
    [
        "HH:mm:ss",
        "H:mm:ss",
        "HH:mm",
        "H:mm"
    ];

    public PlotLimitDialog()
    {
        InitializeComponent();
        ApplyButton.Click += OnApplyClick;
        CancelButton.Click += (_, _) => Close(false);
    }

    public DateTime? CutoffTime { get; private set; }

    public void Load(DateTime? currentCutoff, DateTime? suggestedTime)
    {
        var value = currentCutoff ?? suggestedTime ?? DateTime.Now;
        DateBox.Text = value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        TimeBox.Text = value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        ErrorText.Text = "";
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (!TryParseDateTime(DateBox.Text, TimeBox.Text, out var parsed))
        {
            ErrorText.Text = "Введите дату и время, например 20.05.2026 14:30:00";
            return;
        }

        CutoffTime = parsed;
        Close(true);
    }

    private static bool TryParseDateTime(string? dateText, string? timeText, out DateTime value)
    {
        value = default;

        if (!DateTime.TryParseExact(
                dateText?.Trim(),
                DateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                timeText?.Trim(),
                TimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            return false;
        }

        value = date.Date + time.TimeOfDay;
        return true;
    }
}
