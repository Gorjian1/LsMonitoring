using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LsMonitoring.Core.Configuration;

namespace LsMonitoring.Avalonia;

public partial class SettingsDialog : Window
{
    private AppConfig _config = null!;

    public SettingsDialog()
    {
        InitializeComponent();
        SaveButton.Click += OnSaveClick;
        CancelButton.Click += OnCancelClick;
    }

    public void LoadConfig(AppConfig config)
    {
        _config = config;
        GatewayIpBox.Text = config.Connection.GatewayIp;
        UsernameBox.Text = config.Connection.Username;
        PasswordBox.Text = config.Connection.Password;
        EnableAlarmsBox.IsChecked = config.Alarm.Enabled;

        ThresholdModeBox.SelectedIndex = string.Equals(config.Thresholds.Mode, Thresholds.VariationMode, StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
        ZeroABox.Text = FormatNumber(config.Thresholds.ZeroA);
        ZeroBBox.Text = FormatNumber(config.Thresholds.ZeroB);
        CriticalABox.Text = FormatThreshold(config.Thresholds.CriticalA);
        CriticalBBox.Text = FormatThreshold(config.Thresholds.CriticalB);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _config.Connection.GatewayIp = GatewayIpBox.Text ?? "";
        _config.Connection.Username = UsernameBox.Text ?? "";
        _config.Connection.Password = PasswordBox.Text ?? "";
        _config.Alarm.Enabled = EnableAlarmsBox.IsChecked ?? false;

        _config.Thresholds.Mode = ReadSelectedThresholdMode();
        _config.Thresholds.ZeroA = ParseNumber(ZeroABox.Text, _config.Thresholds.ZeroA);
        _config.Thresholds.ZeroB = ParseNumber(ZeroBBox.Text, _config.Thresholds.ZeroB);
        _config.Thresholds.CriticalA = ParseThreshold(CriticalABox.Text, _config.Thresholds.CriticalA);
        _config.Thresholds.CriticalB = ParseThreshold(CriticalBBox.Text, _config.Thresholds.CriticalB);
        _config.Thresholds.WarningA = AutoWarning(_config.Thresholds.CriticalA);
        _config.Thresholds.WarningB = AutoWarning(_config.Thresholds.CriticalB);
        _config.Thresholds.SameForAb = false;

        Close(true);
    }

    private string ReadSelectedThresholdMode()
    {
        if (ThresholdModeBox.SelectedItem is ComboBoxItem item &&
            item.Tag?.ToString() is { Length: > 0 } mode)
        {
            return mode;
        }

        return Thresholds.AbsoluteMode;
    }

    private static double AutoWarning(double maxDeviation)
    {
        return maxDeviation * 0.8;
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("G", CultureInfo.InvariantCulture);
    }

    private static string FormatThreshold(double value)
    {
        return value.ToString("G", CultureInfo.InvariantCulture);
    }

    private static double ParseNumber(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var normalized = text.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static double ParseThreshold(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var normalized = text.Trim().Replace(',', '.');
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        return fallback;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

}
