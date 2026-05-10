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
        
        EnableTelegramBox.IsChecked = config.Telegram.Enabled;
        TelegramChatIdsBox.Text = string.Join(", ", config.Telegram.ChatIds);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _config.Connection.GatewayIp = GatewayIpBox.Text ?? "";
        _config.Connection.Username = UsernameBox.Text ?? "";
        _config.Connection.Password = PasswordBox.Text ?? "";
        _config.Alarm.Enabled = EnableAlarmsBox.IsChecked ?? false;
        
        _config.Telegram.Enabled = EnableTelegramBox.IsChecked ?? false;
        _config.Telegram.ChatIds = ParseTelegramChatIds(TelegramChatIdsBox.Text ?? "");
        
        Close(true);
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
            await FlashTestButtonAsync("No Bot");
            return;
        }

        var chatIds = ParseTelegramChatIds(TelegramChatIdsBox.Text ?? "");
        var service = new TelegramAlertService(token, chatIds, startPolling: false);

        TestTelegramButton.IsEnabled = false;
        try
        {
            if (chatIds.Count == 0)
            {
                TestTelegramButton.Content = "Press /start";
                await service.DiscoverChatIdsAsync(TimeSpan.FromSeconds(30));
                TelegramChatIdsBox.Text = string.Join(", ", chatIds.Distinct());
            }

            if (chatIds.Count == 0)
            {
                await FlashTestButtonAsync(service.LastError is null ? "No Chat" : "Failed");
                return;
            }

            TestTelegramButton.Content = "Sending...";
            var success = await service.SendTestMessageAsync();
            if (success)
            {
                EnableTelegramBox.IsChecked = true;
            }

            await FlashTestButtonAsync(success ? "Sent!" : "Failed");
        }
        finally
        {
            service.Stop();
            TestTelegramButton.Content = "Test";
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
        TestTelegramButton.Content = "Test";
    }
}
