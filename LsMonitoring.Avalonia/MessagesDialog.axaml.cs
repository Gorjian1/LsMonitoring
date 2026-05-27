using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using LsMonitoring.Core.Alarms;
using LsMonitoring.Core.Configuration;
using LsMonitoring.Core.LocalServices;
using QRCoder;

namespace LsMonitoring.Avalonia;

public partial class MessagesDialog : Window
{
    private const string SendTestText = "Отправить тест";
    private const string TelegramBotUrl = "https://t.me/ls_monitoringbot";

    private readonly CloudflareQuickTunnelService _quickTunnelService = CloudflareQuickTunnelService.CreateDefault();
    private readonly LocalGotifyService _localGotifyService = LocalGotifyService.CreateDefault();

    private AppConfig _config = null!;

    public MessagesDialog()
    {
        InitializeComponent();
        SaveButton.Click += OnSaveClick;
        CancelButton.Click += OnCancelClick;
        BotLinkButton.Click += OnBotLinkClick;
        TestTelegramButton.Click += OnTestTelegramClick;
        TestEmailButton.Click += OnTestEmailClick;
        TestSmsButton.Click += OnTestSmsClick;
        PushDownloadButton.Click += OnPushDownloadClick;
        PushConnectButton.Click += OnPushConnectClick;
        StartPushTunnelButton.Click += OnStartPushTunnelClick;
        TestPushButton.Click += OnTestPushClick;
        PushServerUrlBox.TextChanged += (_, _) => RefreshPushQrCode();
        PushClientTokenBox.TextChanged += (_, _) => RefreshPushQrCode();
        PushAppDownloadUrlBox.TextChanged += (_, _) => RefreshPushQrCode();
        SetQrCode(TelegramBotQrImage, TelegramBotUrl);
    }

    public void LoadConfig(AppConfig config)
    {
        _config = config;
        EnableTelegramBox.IsChecked = config.Telegram.Enabled;
        TelegramChatIdsBox.Text = string.Join(", ", config.Telegram.ChatIds);

        EnableEmailBox.IsChecked = config.Email.Enabled;
        EmailRecipientsBox.Text = string.Join(", ", config.Email.Recipients);
        UseOwnSmtpBox.IsChecked = !config.Email.UsesRelay;
        EmailFromBox.Text = config.Email.From;
        EmailPasswordBox.Text = config.Email.Password;
        EmailSmtpHostBox.Text = config.Email.SmtpHost;
        EmailSmtpPortBox.Text = config.Email.SmtpPort.ToString();
        EmailUsernameBox.Text = string.Equals(config.Email.Username, config.Email.From, StringComparison.OrdinalIgnoreCase)
            ? ""
            : config.Email.Username;
        EmailUseSslBox.IsChecked = config.Email.UseSsl;

        EnableSmsBox.IsChecked = config.Sms.Enabled;
        SmsPhonesBox.Text = string.Join(", ", config.Sms.PhoneNumbers);
        SmsProviderBox.Text = string.IsNullOrWhiteSpace(config.Sms.Provider) ? SmsConfig.SmsRuProvider : config.Sms.Provider;
        SmsApiUrlBox.Text = config.Sms.EffectiveApiUrl;
        SmsApiKeyBox.Text = config.Sms.ApiKey;
        SmsSenderBox.Text = config.Sms.Sender;
        SmsMaxPerHourBox.Text = config.Sms.EffectiveMaxMessagesPerHour.ToString();

        EnablePushBox.IsChecked = config.Push.Enabled;
        PushServerUrlBox.Text = config.Push.ServerUrl;
        PushAppTokenBox.Text = config.Push.AppToken;
        PushClientTokenBox.Text = config.Push.ClientToken;
        PushAppDownloadUrlBox.Text = config.Push.EffectiveAppDownloadUrl;
        PushAutoTunnelBox.IsChecked = config.Push.AutoStartTemporaryTunnel;
        PushLocalServerUrlBox.Text = config.Push.EffectiveLocalServerUrl;
        PushPriorityBox.Text = config.Push.Priority.ToString();
        PushTunnelStatusText.Text = DescribeTemporaryTunnel(config.Push.EffectiveServerUrl);
        RefreshPushQrCode();

        EnableWebhookBox.IsChecked = config.Webhook.Enabled;
        WebhookUrlBox.Text = config.Webhook.Url;
        WebhookSecretBox.Text = config.Webhook.Secret;
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _config.Telegram.Enabled = EnableTelegramBox.IsChecked ?? false;
        _config.Telegram.ChatIds = ParseTelegramChatIds(TelegramChatIdsBox.Text ?? "");

        _config.Email = BuildEmailConfigFromUi();

        _config.Sms.Enabled = EnableSmsBox.IsChecked ?? false;
        _config.Sms.PhoneNumbers = ParseStringList(SmsPhonesBox.Text ?? "");
        _config.Sms.Provider = (SmsProviderBox.Text ?? "").Trim();
        _config.Sms.ApiUrl = (SmsApiUrlBox.Text ?? "").Trim();
        _config.Sms.ApiKey = SmsApiKeyBox.Text ?? "";
        _config.Sms.Sender = (SmsSenderBox.Text ?? "").Trim();
        _config.Sms.MaxMessagesPerHour = ParsePositiveInt(SmsMaxPerHourBox.Text, SmsConfig.DefaultMaxMessagesPerHour);

        _config.Push.Enabled = EnablePushBox.IsChecked ?? false;
        _config.Push.Provider = "Gotify";
        _config.Push.ServerUrl = PushServerUrlBox.Text ?? "";
        _config.Push.AppToken = PushAppTokenBox.Text ?? "";
        _config.Push.ClientToken = PushClientTokenBox.Text ?? "";
        _config.Push.AppDownloadUrl = PushAppDownloadUrlBox.Text ?? "";
        _config.Push.AutoStartTemporaryTunnel = PushAutoTunnelBox.IsChecked ?? false;
        _config.Push.LocalServerUrl = PushLocalServerUrlBox.Text ?? "";
        _config.Push.Priority = ParsePositiveInt(PushPriorityBox.Text, 5);

        _config.Webhook.Enabled = EnableWebhookBox.IsChecked ?? false;
        _config.Webhook.Url = WebhookUrlBox.Text ?? "";
        _config.Webhook.Method = "POST";
        _config.Webhook.Secret = WebhookSecretBox.Text ?? "";

        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private static void OnBotLinkClick(object? sender, RoutedEventArgs e)
    {
        OpenUrl(TelegramBotUrl);
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
            TestTelegramButton.Content = SendTestText;
            TestTelegramButton.IsEnabled = true;
        }
    }

    private async void OnTestEmailClick(object? sender, RoutedEventArgs e)
    {
        var emailConfig = BuildEmailConfigFromUi();
        var service = new EmailAlertService(emailConfig);

        TestEmailButton.IsEnabled = false;
        EmailStatusText.Text = "";

        try
        {
            TestEmailButton.Content = "Отправка...";
            var success = await service.SendTestMessageAsync();
            if (success)
            {
                EnableEmailBox.IsChecked = true;
                EmailStatusText.Text = "Тестовое письмо отправлено.";
            }
            else
            {
                EmailStatusText.Text = service.LastError ?? "Не удалось отправить тестовое письмо.";
            }

            await FlashEmailButtonAsync(success ? "Отправлено!" : "Ошибка");
        }
        finally
        {
            TestEmailButton.Content = SendTestText;
            TestEmailButton.IsEnabled = true;
        }
    }

    private void OnPushDownloadClick(object? sender, RoutedEventArgs e)
    {
        var downloadUrl = BuildPushDownloadTarget();
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            PushStatusText.Text = "Укажите страницу APK в сервисной настройке.";
            return;
        }

        OpenUrl(downloadUrl);
    }

    private async void OnTestSmsClick(object? sender, RoutedEventArgs e)
    {
        var smsConfig = BuildSmsConfigFromUi();
        var service = new SmsAlertService(smsConfig);

        TestSmsButton.IsEnabled = false;
        SmsStatusText.Text = "";

        try
        {
            TestSmsButton.Content = "Отправка...";
            var success = await service.SendTestMessageAsync();
            if (success)
            {
                EnableSmsBox.IsChecked = true;
                SmsStatusText.Text = "Тестовая SMS отправлена.";
            }
            else
            {
                SmsStatusText.Text = service.LastError ?? "Не удалось отправить SMS.";
            }

            await FlashSmsButtonAsync(success ? "Отправлено!" : "Ошибка");
        }
        finally
        {
            TestSmsButton.Content = SendTestText;
            TestSmsButton.IsEnabled = true;
        }
    }

    private void OnPushConnectClick(object? sender, RoutedEventArgs e)
    {
        var serverUrl = (PushServerUrlBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            PushStatusText.Text = "Сначала укажите сервер Gotify.";
            return;
        }

        var clientToken = (PushClientTokenBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(clientToken))
        {
            PushStatusText.Text = "Для мобильного приложения нужен client token.";
            return;
        }

        OpenUrl(BuildPushConnectUri(serverUrl, clientToken));
    }

    private async void OnStartPushTunnelClick(object? sender, RoutedEventArgs e)
    {
        StartPushTunnelButton.IsEnabled = false;
        PushTunnelStatusText.Text = "Запуск локального Gotify...";

        try
        {
            var gotify = await _localGotifyService.EnsureRunningAndBootstrapAsync(
                PushAppTokenBox.Text,
                PushClientTokenBox.Text);
            if (!gotify.Success)
            {
                PushTunnelStatusText.Text = $"Gotify: {gotify.Message}";
                return;
            }

            PushLocalServerUrlBox.Text = gotify.ServerUrl;
            PushAppTokenBox.Text = gotify.AppToken;
            PushClientTokenBox.Text = gotify.ClientToken;

            PushTunnelStatusText.Text = "Запуск временного Cloudflare Tunnel...";
            var result = await _quickTunnelService.EnsureStartedAsync(gotify.ServerUrl);
            if (result.Success)
            {
                PushServerUrlBox.Text = result.PublicUrl;
                EnablePushBox.IsChecked = true;
                PushAutoTunnelBox.IsChecked = true;
                PushTunnelStatusText.Text = "Готово. После перезагрузки компьютера URL пересоздастся автоматически.";
                PushStatusText.Text = result.PublicUrl;
                RefreshPushQrCode();
                return;
            }

            PushTunnelStatusText.Text = result.Message;
        }
        catch (Exception ex)
        {
            PushTunnelStatusText.Text = $"Ошибка tunnel: {ex.Message}";
        }
        finally
        {
            StartPushTunnelButton.IsEnabled = true;
        }
    }

    private void RefreshPushQrCode()
    {
        var downloadTarget = BuildPushDownloadTarget();
        PushDownloadQrText.Text = string.IsNullOrWhiteSpace(downloadTarget) ? "нет ссылки APK" : "готово";
        SetQrCode(PushDownloadQrImage, downloadTarget);

        var connectTarget = BuildPushConnectTarget();
        PushConnectQrText.Text = string.IsNullOrWhiteSpace(connectTarget) ? "нет сервера/token" : "готово";
        SetQrCode(PushConnectQrImage, connectTarget);
    }

    private static void SetQrCode(Image image, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            image.Source = null;
            return;
        }

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
            var pngBytes = new PngByteQRCode(data).GetGraphic(8);
            image.Source = new Bitmap(new MemoryStream(pngBytes));
        }
        catch
        {
            image.Source = null;
        }
    }

    private async void OnTestPushClick(object? sender, RoutedEventArgs e)
    {
        var pushConfig = new PushConfig
        {
            Enabled = EnablePushBox.IsChecked ?? false,
            Provider = "Gotify",
            ServerUrl = PushServerUrlBox.Text ?? "",
            AppToken = PushAppTokenBox.Text ?? "",
            ClientToken = PushClientTokenBox.Text ?? "",
            AppDownloadUrl = PushAppDownloadUrlBox.Text ?? "",
            AutoStartTemporaryTunnel = PushAutoTunnelBox.IsChecked ?? false,
            LocalServerUrl = PushLocalServerUrlBox.Text ?? "",
            Priority = ParsePositiveInt(PushPriorityBox.Text, 5)
        };
        var service = new GotifyAlertService(pushConfig);

        TestPushButton.IsEnabled = false;
        PushStatusText.Text = "";

        try
        {
            TestPushButton.Content = "Отправка...";
            var success = await service.SendTestMessageAsync();
            if (success)
            {
                EnablePushBox.IsChecked = true;
                PushStatusText.Text = "Push отправлен.";
            }
            else
            {
                PushStatusText.Text = service.LastError ?? "Не удалось отправить push.";
            }

            await FlashPushButtonAsync(success ? "Отправлено!" : "Ошибка");
        }
        finally
        {
            TestPushButton.Content = SendTestText;
            TestPushButton.IsEnabled = true;
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

    private static List<string> ParseStringList(string value)
    {
        return value
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private EmailConfig BuildEmailConfigFromUi()
    {
        var config = new EmailConfig
        {
            Enabled = EnableEmailBox.IsChecked ?? false,
            DeliveryMode = UseOwnSmtpBox.IsChecked == true ? EmailDeliveryMode.Smtp : EmailDeliveryMode.Relay,
            InstallationId = _config.Email.InstallationId,
            Recipients = ParseStringList(EmailRecipientsBox.Text ?? ""),
            RelayUrl = _config.Email.RelayUrl,
            From = (EmailFromBox.Text ?? "").Trim(),
            SmtpHost = (EmailSmtpHostBox.Text ?? "").Trim(),
            SmtpPort = ParsePositiveInt(EmailSmtpPortBox.Text, 587),
            UseSsl = EmailUseSslBox.IsChecked ?? true,
            Username = (EmailUsernameBox.Text ?? "").Trim()
        };

        config.RelayToken = _config.Email.RelayToken;
        config.Password = EmailPasswordBox.Text ?? "";
        return config;
    }

    private SmsConfig BuildSmsConfigFromUi()
    {
        return new SmsConfig
        {
            Enabled = EnableSmsBox.IsChecked ?? false,
            Provider = (SmsProviderBox.Text ?? "").Trim(),
            ApiUrl = (SmsApiUrlBox.Text ?? "").Trim(),
            ApiKey = SmsApiKeyBox.Text ?? "",
            Sender = (SmsSenderBox.Text ?? "").Trim(),
            PhoneNumbers = ParseStringList(SmsPhonesBox.Text ?? ""),
            MaxMessagesPerHour = ParsePositiveInt(SmsMaxPerHourBox.Text, SmsConfig.DefaultMaxMessagesPerHour)
        };
    }

    private static int ParsePositiveInt(string? value, int fallback)
    {
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private string BuildPushDownloadTarget()
    {
        var value = (PushAppDownloadUrlBox.Text ?? "").Trim();
        return string.IsNullOrWhiteSpace(value)
            ? PushConfig.DefaultAppDownloadUrl
            : value;
    }

    private string BuildPushConnectTarget()
    {
        var serverUrl = (PushServerUrlBox.Text ?? "").Trim();
        var clientToken = (PushClientTokenBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(clientToken))
        {
            return "";
        }

        return BuildPushConnectUri(serverUrl, clientToken);
    }

    private static string BuildPushConnectUri(string serverUrl, string clientToken)
    {
        return $"lsmonitoring://connect?server={Uri.EscapeDataString(serverUrl)}&token={Uri.EscapeDataString(clientToken)}";
    }

    private static string DescribeTemporaryTunnel(string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return "Tunnel не запущен.";
        }

        if (Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var uri) &&
            uri.Host.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase))
        {
            return "Используется временный Cloudflare Tunnel. После перезагрузки компьютера URL меняется.";
        }

        return "Задан постоянный публичный URL; временный tunnel не будет заменять его автоматически.";
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Link opening is a UI convenience and should not block the dialog.
        }
    }

    private async Task FlashTestButtonAsync(string content)
    {
        TestTelegramButton.Content = content;
        await Task.Delay(2000);
        TestTelegramButton.Content = SendTestText;
    }

    private async Task FlashEmailButtonAsync(string content)
    {
        TestEmailButton.Content = content;
        await Task.Delay(2000);
        TestEmailButton.Content = SendTestText;
    }

    private async Task FlashSmsButtonAsync(string content)
    {
        TestSmsButton.Content = content;
        await Task.Delay(2000);
        TestSmsButton.Content = SendTestText;
    }

    private async Task FlashPushButtonAsync(string content)
    {
        TestPushButton.Content = content;
        await Task.Delay(2000);
        TestPushButton.Content = SendTestText;
    }
}
