using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using LsMonitoring.Core.Configuration;

namespace LsMonitoring.Core.Alarms;

public sealed class ActiveEmailAlarm
{
    public DateTime StartTime { get; set; }
    public double LastValue { get; set; }
}

public sealed class EmailAlertService : IAlertChannel
{
    private const string LogFilePath = "logs/email_debug.txt";

    private readonly EmailConfig _config;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<(int NodeId, string Axis), ActiveEmailAlarm> _activeAlarms = [];

    public string? LastError { get; private set; }
    public string ChannelName => "Email";

    public EmailAlertService(EmailConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<bool> SendTestMessageAsync()
    {
        return await SendEmailAsync(
            "LS Monitoring: тестовое письмо",
            "Тестовое письмо от LS Monitoring.\n\nЕсли вы видите это сообщение, почтовые уведомления настроены.");
    }

    public Task<bool> SendTestAsync()
    {
        return SendTestMessageAsync();
    }

    public Task NotifyStartedAsync(AlertEvent alertEvent)
    {
        return UpdateAlarmAsync(alertEvent.NodeId, alertEvent.Axis, true, alertEvent.CurrentValue, alertEvent.StartedAt);
    }

    public Task NotifyResolvedAsync(AlertEvent alertEvent)
    {
        return UpdateAlarmAsync(
            alertEvent.NodeId,
            alertEvent.Axis,
            false,
            alertEvent.CurrentValue,
            alertEvent.ResolvedAt ?? alertEvent.UpdatedAt);
    }

    public async Task UpdateAlarmAsync(int nodeId, string axis, bool isCritical, double value, DateTime timestamp)
    {
        if (!_config.Enabled)
        {
            return;
        }

        var key = (nodeId, axis);

        if (isCritical)
        {
            if (_activeAlarms.TryGetValue(key, out var active))
            {
                active.LastValue = value;
                return;
            }

            var sent = await SendEmailAsync(
                $"LS Monitoring: тревога, узел {nodeId}, ось {axis}",
                FormatAlarmMessage(nodeId, axis, value, timestamp));

            if (sent)
            {
                _activeAlarms[key] = new ActiveEmailAlarm
                {
                    StartTime = timestamp,
                    LastValue = value
                };
            }

            return;
        }

        if (_activeAlarms.TryGetValue(key, out var alarm))
        {
            var sent = await SendEmailAsync(
                $"LS Monitoring: норма, узел {nodeId}, ось {axis}",
                FormatResolvedMessage(nodeId, axis, alarm.StartTime, timestamp));

            if (sent)
            {
                _activeAlarms.Remove(key);
            }
        }
    }

    private async Task<bool> SendEmailAsync(string subject, string body)
    {
        return _config.UsesRelay
            ? await SendRelayEmailAsync(subject, body)
            : await SendSmtpEmailAsync(subject, body);
    }

    private async Task<bool> SendRelayEmailAsync(string subject, string body)
    {
        LastError = null;

        if (!_config.HasRelaySettings)
        {
            RecordError("Не задана сервисная почта: нужны получатели, Relay URL и ключ установки.");
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _config.RelayUrl.Trim());
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.RelayToken);
            request.Headers.TryAddWithoutValidation("X-LS-Installation-Id", _config.InstallationId);

            var payload = new EmailRelayRequest(
                _config.InstallationId,
                _config.Recipients,
                subject,
                body);

            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                LogDiagnostic($"relay-email-sent url={_config.RelayUrl} recipients={_config.Recipients.Count} subject={subject}");
                return true;
            }

            RecordError($"relay HTTP {(int)response.StatusCode} {response.StatusCode}: {Truncate(responseBody)}");
            return false;
        }
        catch (Exception ex)
        {
            RecordError($"relay exception: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> SendSmtpEmailAsync(string subject, string body)
    {
        LastError = null;

        if (!_config.HasDeliverySettings)
        {
            RecordError("Не задан SMTP: нужны получатели, отправитель и SMTP-сервер.");
            return false;
        }

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_config.EffectiveFrom, "LS Monitoring", Encoding.UTF8),
                Subject = subject,
                SubjectEncoding = Encoding.UTF8,
                Body = body,
                BodyEncoding = Encoding.UTF8,
                IsBodyHtml = false
            };

            foreach (var recipient in _config.Recipients.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                message.To.Add(new MailAddress(recipient));
            }

            if (message.To.Count == 0)
            {
                RecordError("Не указаны корректные получатели email.");
                return false;
            }

            using var smtp = new SmtpClient(_config.EffectiveSmtpHost, _config.EffectiveSmtpPort)
            {
                DeliveryMethod = SmtpDeliveryMethod.Network,
                EnableSsl = _config.UseSsl,
                Timeout = 15000,
                UseDefaultCredentials = false
            };

            var username = _config.EffectiveUsername;
            var password = _config.Password;
            if (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password))
            {
                smtp.Credentials = new NetworkCredential(username, password);
            }

            await smtp.SendMailAsync(message);
            LogDiagnostic($"smtp-email-sent host={_config.EffectiveSmtpHost}:{_config.EffectiveSmtpPort} recipients={message.To.Count} subject={subject}");
            return true;
        }
        catch (Exception ex)
        {
            RecordError($"smtp exception: {ex.Message}");
            return false;
        }
    }

    private static string FormatAlarmMessage(int nodeId, string axis, double value, DateTime startTime)
    {
        return
            "LS Monitoring\n\n" +
            $"Узел: {nodeId}\n" +
            $"Ось: {axis}\n" +
            "Статус: критическое отклонение\n" +
            $"Отклонение: {value:F3}°\n" +
            $"Начало: {startTime:dd.MM.yyyy HH:mm:ss}";
    }

    private static string FormatResolvedMessage(int nodeId, string axis, DateTime startTime, DateTime resolveTime)
    {
        var duration = resolveTime - startTime;
        return
            "LS Monitoring\n\n" +
            $"Узел: {nodeId}\n" +
            $"Ось: {axis}\n" +
            "Статус: значение вернулось в норму\n" +
            $"Начало события: {startTime:dd.MM.yyyy HH:mm:ss}\n" +
            $"Завершено: {resolveTime:dd.MM.yyyy HH:mm:ss}\n" +
            $"Длительность: {duration:hh\\:mm\\:ss}";
    }

    private void RecordError(string message)
    {
        LastError = message;
        LogDiagnostic(message);
    }

    public static void LogDiagnostic(string message)
    {
        try
        {
            Directory.CreateDirectory("logs");
            File.AppendAllText(LogFilePath, $"[{DateTime.Now}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must not break alert delivery.
        }
    }

    private static string Truncate(string value)
    {
        const int maxLength = 500;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private sealed record EmailRelayRequest(
        string InstallationId,
        IReadOnlyList<string> Recipients,
        string Subject,
        string Body);
}
