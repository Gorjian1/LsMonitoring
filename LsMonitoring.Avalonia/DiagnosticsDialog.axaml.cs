using System.Diagnostics;
using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LsMonitoring.Core.Updates;

namespace LsMonitoring.Avalonia;

public sealed record ChannelDiagnostics(
    string Name,
    bool Enabled,
    bool Ready,
    bool SupportsActiveUpdate,
    string? LastError);

public sealed class DiagnosticsSnapshot
{
    public required string ConfigPath { get; init; }
    public required string DeviationHistoryPath { get; init; }
    public required string GatewayIp { get; init; }
    public required string GatewayState { get; init; }
    public required string StatusLine { get; init; }
    public bool IsPolling { get; init; }
    public int NodeCount { get; init; }
    public int TotalMessages { get; init; }
    public int ActiveDeviationCount { get; init; }
    public required string PublicPushUrl { get; init; }
    public IReadOnlyList<ChannelDiagnostics> Channels { get; init; } = [];
}

public partial class DiagnosticsDialog : Window
{
    private string _releaseUrl = GitHubReleaseChecker.RepositoryReleasesUrl;

    public DiagnosticsDialog()
    {
        InitializeComponent();
        CheckUpdateButton.Click += OnCheckUpdateClick;
        OpenReleaseButton.Click += (_, _) => OpenUrl(_releaseUrl);
        CloseButton.Click += OnCloseClick;
    }

    public void Load(DiagnosticsSnapshot snapshot)
    {
        AppVersionText.Text = CurrentAppVersion();
        ConfigPathText.Text = snapshot.ConfigPath;
        HistoryPathText.Text = snapshot.DeviationHistoryPath;
        GatewayText.Text = snapshot.GatewayIp;
        GatewayStateText.Text = snapshot.GatewayState;
        PollingText.Text = snapshot.IsPolling
            ? $"идёт, узлов: {snapshot.NodeCount}, сообщений: {snapshot.TotalMessages}"
            : $"остановлен, узлов: {snapshot.NodeCount}, сообщений: {snapshot.TotalMessages}";
        StatusLineText.Text = string.IsNullOrWhiteSpace(snapshot.StatusLine)
            ? "-"
            : snapshot.StatusLine;

        TelegramChannelText.Text = FormatChannel(snapshot.Channels, "Telegram");
        EmailChannelText.Text = FormatChannel(snapshot.Channels, "Почта");
        SmsChannelText.Text = FormatChannel(snapshot.Channels, "SMS");
        PushChannelText.Text = FormatChannel(snapshot.Channels, "Push");
        ChannelErrorsText.Text = FormatChannelErrors(snapshot.Channels);

        PublicPushUrlText.Text = string.IsNullOrWhiteSpace(snapshot.PublicPushUrl)
            ? "не задан"
            : snapshot.PublicPushUrl;
        TunnelStatusText.Text = DescribeTunnel(snapshot.PublicPushUrl);
        GotifyStatusText.Text = "не проверен";
        _ = RefreshGotifyStatusAsync(snapshot.PublicPushUrl);

        if (snapshot.ActiveDeviationCount > 0)
        {
            StatusLineText.Text = $"{StatusLineText.Text} | активных отклонений: {snapshot.ActiveDeviationCount}";
        }
    }

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        OpenReleaseButton.IsEnabled = false;
        UpdateStatusText.Text = "Проверка GitHub Releases...";

        try
        {
            var result = await GitHubReleaseChecker.CheckAsync(CurrentAppVersion());
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                UpdateStatusText.Text = result.Error;
                return;
            }

            _releaseUrl = string.IsNullOrWhiteSpace(result.ReleaseUrl)
                ? GitHubReleaseChecker.RepositoryReleasesUrl
                : result.ReleaseUrl;
            OpenReleaseButton.IsEnabled = true;

            if (result.IsUpdateAvailable)
            {
                UpdateStatusText.Text = $"Доступна версия {result.LatestVersion}. Текущая версия: {result.CurrentVersion}.";
                OpenReleaseButton.Content = "Открыть релиз";
            }
            else
            {
                UpdateStatusText.Text = $"Версия актуальна: {result.CurrentVersion}.";
                OpenReleaseButton.Content = "Открыть релизы";
            }
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task RefreshGotifyStatusAsync(string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            GotifyStatusText.Text = "не настроен";
            return;
        }

        if (!Uri.TryCreate(serverUrl.Trim().TrimEnd('/') + "/health", UriKind.Absolute, out var healthUri))
        {
            GotifyStatusText.Text = "некорректный URL";
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var response = await http.GetAsync(healthUri);
            GotifyStatusText.Text = response.IsSuccessStatusCode
                ? $"доступен, HTTP {(int)response.StatusCode}"
                : $"ответил HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex)
        {
            GotifyStatusText.Text = $"не доступен: {ex.Message}";
        }
    }

    private static string FormatChannel(IEnumerable<ChannelDiagnostics> channels, string name)
    {
        var channel = channels.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (channel is null)
        {
            return "нет данных";
        }

        if (!channel.Enabled)
        {
            return "выключен";
        }

        var state = channel.Ready ? "включён, готов" : "включён, не хватает настроек";
        var mode = channel.SupportsActiveUpdate
            ? "активное уведомление обновляется"
            : "отправляются только старт и завершение";
        return $"{state}; {mode}";
    }

    private static string FormatChannelErrors(IEnumerable<ChannelDiagnostics> channels)
    {
        var errors = channels
            .Where(x => !string.IsNullOrWhiteSpace(x.LastError))
            .Select(x => $"{x.Name}: {x.LastError}")
            .ToList();
        return errors.Count == 0 ? "ошибок нет" : string.Join(Environment.NewLine, errors);
    }

    private static string DescribeTunnel(string publicPushUrl)
    {
        if (string.IsNullOrWhiteSpace(publicPushUrl))
        {
            return "не задан";
        }

        if (!Uri.TryCreate(publicPushUrl, UriKind.Absolute, out var uri))
        {
            return "URL не распознан";
        }

        if (IsLocalHost(uri.Host))
        {
            return "локальный адрес, вне этой сети push не заработает";
        }

        if (uri.Host.EndsWith(".lhr.life", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase))
        {
            return "временный tunnel (localhost.run); после перезагрузки URL нужно создать заново";
        }

        return string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
            ? "публичный HTTPS URL задан"
            : "публичный URL задан, но лучше использовать HTTPS";
    }

    private static bool IsLocalHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return IPAddress.IsLoopback(address) ||
               bytes is [10, ..] ||
               bytes is [172, >= 16 and <= 31, ..] ||
               bytes is [192, 168, ..] ||
               bytes is [169, 254, ..];
    }

    private static string CurrentAppVersion()
    {
        var assembly = typeof(DiagnosticsDialog).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Split('+')[0];
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
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
            // Opening a browser is optional diagnostics behavior.
        }
    }
}
