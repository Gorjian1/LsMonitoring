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
    private const string UnlinkChatText = "Отвязать чат";

    private static readonly char[] s_chatIdSeparators = [',', ';', ' '];
    private static readonly char[] s_listSeparators = [',', ';', '\r', '\n'];

    // Owned by MainWindow (one gotify/tunnel owner per app); the dialog only borrows them and
    // must NOT dispose them.
    private readonly LocalhostRunTunnelService _quickTunnelService;
    private readonly LocalGotifyService _localGotifyService;
    private readonly TelegramCompanionService? _companion;
    // Shared by the channel "test" buttons so repeated clicks don't leak an HttpClient each time.
    private readonly HttpClient _testHttpClient = new();

    private AppConfig _config = null!;

    public event Action? ConfigChanged;

    // Parameterless ctor required by the Avalonia runtime XAML loader / previewer.
    public MessagesDialog() : this(null, LocalhostRunTunnelService.CreateDefault(), LocalGotifyService.CreateDefault())
    {
    }

    public MessagesDialog(
        TelegramCompanionService? companion,
        LocalhostRunTunnelService quickTunnelService,
        LocalGotifyService localGotifyService)
    {
        _companion = companion;
        _quickTunnelService = quickTunnelService;
        _localGotifyService = localGotifyService;
        InitializeComponent();
        SaveButton.Click += OnSaveClick;
        CancelButton.Click += OnCancelClick;
        BotLinkButton.Click += OnBotLinkClick;
        TestTelegramButton.Click += OnTestTelegramClick;
        UnlinkTelegramButton.Click += OnUnlinkTelegramClick;
        TestEmailButton.Click += OnTestEmailClick;
        TestSmsButton.Click += OnTestSmsClick;
        PushDownloadButton.Click += OnPushDownloadClick;
        PushConnectButton.Click += OnPushConnectClick;
        StartPushTunnelButton.Click += OnStartPushTunnelClick;
        TestPushButton.Click += OnTestPushClick;
        PushServerUrlBox.TextChanged += (_, _) => RefreshPushQrCode();
        PushClientTokenBox.TextChanged += (_, _) => RefreshPushQrCode();
        PushAppDownloadUrlBox.TextChanged += (_, _) => RefreshPushQrCode();
    }

    protected override void OnClosed(EventArgs e)
    {
        _testHttpClient.Dispose();
        base.OnClosed(e);
    }

    public void LoadConfig(AppConfig config)
    {
        _config = config;
        TelegramLinkCodeBox.Text = config.Telegram.EffectiveLinkCode;
        SetQrCode(TelegramBotQrImage, BuildTelegramBotUrl(config.Telegram.EffectiveLinkCode));
        BotLinkButton.Content = $"@{TelegramSecrets.DefaultBotUsername}";
        EnableTelegramBox.IsChecked = config.Telegram.Enabled;
        TelegramChatIdsBox.Text = string.Join(", ", config.Telegram.ChatIds);

        EnableEmailBox.IsChecked = config.Email.Enabled;
        EmailRecipientsBox.Text = string.Join(", ", config.Email.Recipients);
        EmailSendResolvedBox.IsChecked = config.Email.SendResolvedNotifications;
        UseOwnSmtpBox.IsChecked = !config.Email.UsesService;
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
        SmsSendResolvedBox.IsChecked = config.Sms.SendResolvedNotifications;
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
        _config.Sms.SendResolvedNotifications = SmsSendResolvedBox.IsChecked ?? false;
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

    private void OnBotLinkClick(object? sender, RoutedEventArgs e)
    {
        OpenUrl(BuildTelegramBotUrl(_config.Telegram.EffectiveLinkCode));
    }

    private async void OnTestTelegramClick(object? sender, RoutedEventArgs e)
    {
        var token = _config.Telegram.EffectiveBotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            await FlashTestButtonAsync("Бот не встроен в эту сборку");
            return;
        }

        if (_companion is null)
        {
            await FlashTestButtonAsync("Бот недоступен");
            return;
        }

        var chatIds = ParseTelegramChatIds(TelegramChatIdsBox.Text ?? "");
        var linkCode = _config.Telegram.EffectiveLinkCode;
        TelegramLinkCodeBox.Text = linkCode;
        SetQrCode(TelegramBotQrImage, BuildTelegramBotUrl(linkCode));

        TestTelegramButton.IsEnabled = false;
        try
        {
            if (!await _companion.EnsureConfiguredAsync(token, linkCode, chatIds))
            {
                await FlashTestButtonAsync("Ошибка бота");
                return;
            }

            if (chatIds.Count == 0)
            {
                TestTelegramButton.Content = "Нажмите /start";
                var bound = await WaitForChatBindingAsync(TimeSpan.FromSeconds(30));
                if (bound.Count > 0)
                {
                    chatIds = bound.ToList();
                    TelegramChatIdsBox.Text = string.Join(", ", chatIds.Distinct());
                    PersistTelegramStateFromUi();
                }
            }

            if (chatIds.Count == 0)
            {
                await FlashTestButtonAsync(_companion.LastError is null ? "Нет чатов" : "Ошибка");
                return;
            }

            TestTelegramButton.Content = "Отправка...";
            var success = await _companion.SendTestAsync();
            if (success)
            {
                EnableTelegramBox.IsChecked = true;
                PersistTelegramStateFromUi();
            }

            await FlashTestButtonAsync(success ? "Отправлено!" : "Ошибка");
        }
        finally
        {
            TestTelegramButton.Content = SendTestText;
            TestTelegramButton.IsEnabled = true;
        }
    }

    private async Task<IReadOnlyList<long>> WaitForChatBindingAsync(TimeSpan timeout)
    {
        if (_companion is null)
        {
            return [];
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var bound = await _companion.GetBoundChatIdsAsync();
            if (bound.Count > 0)
            {
                return bound;
            }

            await Task.Delay(1000);
        }

        return await _companion.GetBoundChatIdsAsync();
    }

    private async void OnUnlinkTelegramClick(object? sender, RoutedEventArgs e)
    {
        TelegramChatIdsBox.Text = "";
        EnableTelegramBox.IsChecked = false;
        PersistTelegramStateFromUi();
        UnlinkTelegramButton.IsEnabled = false;
        try
        {
            UnlinkTelegramButton.Content = "Отвязано";
            await Task.Delay(1500);
        }
        finally
        {
            UnlinkTelegramButton.Content = UnlinkChatText;
            UnlinkTelegramButton.IsEnabled = true;
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
                emailConfig.Enabled = true;
                _config.Email = emailConfig;
                ConfigChanged?.Invoke();
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
        var service = new SmsAlertService(smsConfig, _testHttpClient);

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

            PushTunnelStatusText.Text = "Запуск временного tunnel (localhost.run)...";
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
        var service = new GotifyAlertService(pushConfig, _testHttpClient);

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
        var parts = value.Split(s_chatIdSeparators, StringSplitOptions.RemoveEmptyEntries);
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
            .Split(s_listSeparators, StringSplitOptions.RemoveEmptyEntries)
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
            DeliveryMode = UseOwnSmtpBox.IsChecked == true ? EmailDeliveryMode.Smtp : EmailDeliveryMode.Service,
            Recipients = ParseStringList(EmailRecipientsBox.Text ?? ""),
            SendResolvedNotifications = EmailSendResolvedBox.IsChecked ?? false,
            From = (EmailFromBox.Text ?? "").Trim(),
            SmtpHost = (EmailSmtpHostBox.Text ?? "").Trim(),
            SmtpPort = ParsePositiveInt(EmailSmtpPortBox.Text, 587),
            UseSsl = EmailUseSslBox.IsChecked ?? true,
            Username = (EmailUsernameBox.Text ?? "").Trim()
        };

        config.Password = EmailPasswordBox.Text ?? "";
        return config;
    }

    private void PersistTelegramStateFromUi()
    {
        _config.Telegram.Enabled = EnableTelegramBox.IsChecked ?? false;
        _config.Telegram.ChatIds = ParseTelegramChatIds(TelegramChatIdsBox.Text ?? "");
        ConfigChanged?.Invoke();
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
            SendResolvedNotifications = SmsSendResolvedBox.IsChecked ?? false,
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

    private static string BuildTelegramBotUrl(string linkCode)
    {
        var baseUrl = $"https://t.me/{TelegramSecrets.DefaultBotUsername}";
        return string.IsNullOrWhiteSpace(linkCode)
            ? baseUrl
            : $"{baseUrl}?start={Uri.EscapeDataString(linkCode.Trim())}";
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
            (uri.Host.EndsWith(".lhr.life", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase)))
        {
            return "Используется временный tunnel (localhost.run). После перезагрузки компьютера URL меняется.";
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
