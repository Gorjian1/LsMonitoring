using System.Text.Json;
using LsMonitoring.Core.Configuration;

namespace LsMonitoring.Core.Alarms;

public sealed class ActiveSmsAlarm
{
    public DateTime StartTime { get; set; }
    public double LastValue { get; set; }
}

public sealed class SmsAlertService : IAlertChannel
{
    private const string LogFilePath = "logs/sms_debug.txt";

    private readonly SmsConfig _config;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<(int NodeId, string Axis), ActiveSmsAlarm> _activeAlarms = [];
    private readonly Queue<DateTime> _sentAt = [];

    public SmsAlertService(SmsConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _httpClient = httpClient ?? new HttpClient();
    }

    public string ChannelName => "SMS";
    public string? LastError { get; private set; }

    public Task<bool> SendTestMessageAsync()
    {
        return SendSmsAsync("LS Monitoring: тест SMS. Если сообщение пришло, SMS-канал настроен.");
    }

    public Task<bool> SendTestAsync()
    {
        return SendTestMessageAsync();
    }

    public async Task NotifyStartedAsync(AlertEvent alertEvent)
    {
        await UpdateAlarmAsync(alertEvent.NodeId, alertEvent.Axis, true, alertEvent.CurrentValue, alertEvent.StartedAt);
    }

    public async Task NotifyResolvedAsync(AlertEvent alertEvent)
    {
        await UpdateAlarmAsync(
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

            var sent = await SendSmsAsync(FormatAlarmMessage(nodeId, axis, value, timestamp));
            if (sent)
            {
                _activeAlarms[key] = new ActiveSmsAlarm
                {
                    StartTime = timestamp,
                    LastValue = value
                };
            }

            return;
        }

        if (_activeAlarms.TryGetValue(key, out var alarm))
        {
            var sent = await SendSmsAsync(FormatResolvedMessage(nodeId, axis, alarm.StartTime, timestamp));
            if (sent)
            {
                _activeAlarms.Remove(key);
            }
        }
    }

    private async Task<bool> SendSmsAsync(string message)
    {
        LastError = null;

        if (!_config.HasDeliverySettings)
        {
            RecordError("Не задан SMS: нужны API key и номера телефонов.");
            return false;
        }

        var recipients = _config.PhoneNumbers
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (recipients.Count == 0)
        {
            RecordError("Не указаны номера SMS.");
            return false;
        }

        var allSuccess = true;
        foreach (var phone in recipients)
        {
            if (!TryReserveRateLimitSlot())
            {
                allSuccess = false;
                continue;
            }

            var success = await SendOneSmsAsync(phone, message);
            allSuccess &= success;
        }

        return allSuccess;
    }

    private async Task<bool> SendOneSmsAsync(string phone, string message)
    {
        try
        {
            using var response = await _httpClient.GetAsync(BuildSmsRuUri(phone, message));
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                RecordError($"SMS HTTP {(int)response.StatusCode} {response.StatusCode}: {Truncate(body)}");
                return false;
            }

            if (LooksLikeSmsRuError(body))
            {
                RecordError($"SMS provider error: {Truncate(body)}");
                return false;
            }

            LogDiagnostic($"sms-sent provider={_config.EffectiveProvider} phone={MaskPhone(phone)}");
            return true;
        }
        catch (Exception ex)
        {
            RecordError($"SMS exception: {ex.Message}");
            return false;
        }
    }

    private Uri BuildSmsRuUri(string phone, string message)
    {
        var query = new List<string>
        {
            $"api_id={Uri.EscapeDataString(_config.ApiKey.Trim())}",
            $"to={Uri.EscapeDataString(phone)}",
            $"msg={Uri.EscapeDataString(message)}",
            "json=1"
        };

        if (!string.IsNullOrWhiteSpace(_config.Sender))
        {
            query.Add($"from={Uri.EscapeDataString(_config.Sender.Trim())}");
        }

        var separator = _config.EffectiveApiUrl.Contains('?') ? "&" : "?";
        return new Uri(_config.EffectiveApiUrl + separator + string.Join("&", query));
    }

    private bool TryReserveRateLimitSlot()
    {
        var now = DateTime.UtcNow;
        while (_sentAt.Count > 0 && now - _sentAt.Peek() > TimeSpan.FromHours(1))
        {
            _sentAt.Dequeue();
        }

        var limit = _config.EffectiveMaxMessagesPerHour;
        if (_sentAt.Count >= limit)
        {
            RecordError($"SMS limit exceeded: максимум {limit} сообщений в час.");
            return false;
        }

        _sentAt.Enqueue(now);
        return true;
    }

    private static bool LooksLikeSmsRuError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("status", out var status) &&
                   string.Equals(status.GetString(), "ERROR", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return body.Contains("\"status\":\"ERROR\"", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FormatAlarmMessage(int nodeId, string axis, double value, DateTime startTime)
    {
        return $"LS Monitoring: тревога. Узел {nodeId}, ось {axis}, отклонение {value:F3}°, начало {startTime:dd.MM HH:mm:ss}.";
    }

    private static string FormatResolvedMessage(int nodeId, string axis, DateTime startTime, DateTime resolveTime)
    {
        var duration = resolveTime - startTime;
        return $"LS Monitoring: норма. Узел {nodeId}, ось {axis}, длительность {duration:hh\\:mm\\:ss}.";
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

    private static string MaskPhone(string phone)
    {
        return phone.Length <= 4 ? "****" : new string('*', Math.Max(0, phone.Length - 4)) + phone[^4..];
    }

    private static string Truncate(string value)
    {
        const int maxLength = 500;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
