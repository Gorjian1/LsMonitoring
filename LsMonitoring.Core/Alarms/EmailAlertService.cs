using LsMonitoring.Core.Configuration;
using LsMonitoring.Core.IO;

namespace LsMonitoring.Core.Alarms;

public sealed class ActiveEmailAlarm
{
    public DateTime StartTime { get; set; }
    public double LastValue { get; set; }
}

public sealed class EmailAlertService : IAlertChannel
{
    private static readonly string LogFilePath = AppPaths.LogFile("email_debug.txt");

    private readonly EmailConfig _config;
    private readonly IEmailSender _sender;
    private readonly Dictionary<(int NodeId, string Axis), ActiveEmailAlarm> _activeAlarms = [];
    private readonly SemaphoreSlim _alarmGate = new(1, 1);

    public string? LastError { get; private set; }
    public string ChannelName => "Email";

    public EmailAlertService(EmailConfig config, IEmailSender? sender = null)
    {
        _config = config;
        _sender = sender ?? new SmtpEmailSender();
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
        await _alarmGate.WaitAsync();
        try
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
                if (!_config.SendResolvedNotifications)
                {
                    _activeAlarms.Remove(key);
                    return;
                }

                var sent = await SendEmailAsync(
                    $"LS Monitoring: норма, узел {nodeId}, ось {axis}",
                    FormatResolvedMessage(nodeId, axis, alarm.StartTime, timestamp));

                if (sent)
                {
                    _activeAlarms.Remove(key);
                }
            }
        }
        finally
        {
            _alarmGate.Release();
        }
    }

    private async Task<bool> SendEmailAsync(string subject, string body)
    {
        LastError = null;

        var transport = _config.ResolveTransport();
        if (transport is null)
        {
            RecordError(_config.UsesService
                ? "Сервисная почта не настроена в этой сборке."
                : "Не задан альтернативный SMTP: нужны отправитель и сервер.");
            return false;
        }

        var recipients = _config.Recipients
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (recipients.Count == 0)
        {
            RecordError("Не указаны корректные получатели email.");
            return false;
        }

        var result = await _sender.SendAsync(transport, subject, body, recipients);
        if (result.Success)
        {
            LogDiagnostic($"email-sent host={transport.Host}:{transport.Port} recipients={recipients.Count} subject={subject}");
            return true;
        }

        RecordError($"smtp exception: {result.Error}");
        return false;
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
            File.AppendAllText(LogFilePath, $"[{DateTime.Now}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must not break alert delivery.
        }
    }

}
