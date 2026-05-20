using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LsMonitoring.Core.Alarms;
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
        TestTelegramButton.Click += OnTestTelegramClick;
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

        EnableTelegramBox.IsChecked = config.Telegram.Enabled;
        TelegramChatIdsBox.Text = string.Join(", ", config.Telegram.ChatIds);
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

        _config.Telegram.Enabled = EnableTelegramBox.IsChecked ?? false;
        _config.Telegram.ChatIds = ParseTelegramChatIds(TelegramChatIdsBox.Text ?? "");

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

    private async void OnTestTelegramClick(object? sender, RoutedEventArgs e)
    {
        var token = _config.Telegram.EffectiveBotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            await FlashTestButtonAsync("Нет бота");
            return;
        }

        var chatIds = ParseTelegramChatIds(TelegramChatIdsBox.Text ?? "");
        var service = new TelegramAlertService(token, chatIds, startPolling: false);

        TestTelegramButton.IsEnabled = false;
        try
        {
            if (chatIds.Count == 0)
            {
                TestTelegramButton.Content = "Нажмите /start";
                await service.DiscoverChatIdsAsync(TimeSpan.FromSeconds(30));
                TelegramChatIdsBox.Text = string.Join(", ", chatIds.Distinct());
            }

            if (chatIds.Count == 0)
            {
                await FlashTestButtonAsync(service.LastError is null ? "Нет чатов" : "Ошибка");
                return;
            }

            TestTelegramButton.Content = "Отправка...";
            var success = await service.SendTestMessageAsync();
            if (success)
            {
                EnableTelegramBox.IsChecked = true;
            }

            await FlashTestButtonAsync(success ? "Отправлено!" : "Ошибка");
        }
        finally
        {
            service.Stop();
            TestTelegramButton.Content = "Тест";
            TestTelegramButton.IsEnabled = true;
        }
    }

    private static List<long> ParseTelegramChatIds(string value)
    {
        var chatIds = new List<long>();
        var parts = value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (long.TryParse(part, out var id) && !chatIds.Contains(id))
            {
                chatIds.Add(id);
            }
        }

        return chatIds;
    }

    private async Task FlashTestButtonAsync(string content)
    {
        TestTelegramButton.Content = content;
        await Task.Delay(2000);
        TestTelegramButton.Content = "Тест";
    }
}
