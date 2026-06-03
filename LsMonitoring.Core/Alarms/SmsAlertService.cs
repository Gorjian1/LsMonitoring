using System.Net.Http;
using System.Text.Json;
using LsMonitoring.Core.Configuration;
using LsMonitoring.Core.IO;

namespace LsMonitoring.Core.Alarms;

public sealed class ActiveSmsAlarm
{
    public DateTime StartTime { get; set; }
    public double LastValue { get; set; }
}

public sealed class SmsAlertService : IAlertChannel
{
    private static readonly string LogFilePath = AppPaths.LogFile("sms_debug.txt");

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
            if (!_config.SendResolvedNotifications)
            {
                _activeAlarms.Remove(key);
                return;
            }

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
            // Check capacity without consuming the slot — only commit on success so that
            // network errors and provider-side failures don't drain the hourly quota.
            if (!CheckRateLimitCapacity())
            {
                RecordError($"SMS limit exceeded: максимум {_config.EffectiveMaxMessagesPerHour} сообщений в час.");
                allSuccess = false;
                continue;
            }

            var success = await SendOneSmsAsync(phone, message);
            if (success)
            {
                CommitRateLimitSlot();
            }

            allSuccess &= success;
        }

        return allSuccess;
    }

    private async Task<bool> SendOneSmsAsync(string phone, string message)
    {
        try
        {
            var values = new Dictionary<string, string>
            {
                { "api_id", _config.ApiKey.Trim() },
                { "to", phone },
                { "msg", message },
                { "json", "1" }
            };

            if (!string.IsNullOrWhiteSpace(_config.Sender))
            {
                values["from"] = _config.Sender.Trim();
            }

            using var content = new FormUrlEncodedContent(values);
            using var response = await _httpClient.PostAsync(_config.EffectiveApiUrl, content);
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

    /// <summary>
    /// Returns <c>true</c> when the hourly window still has capacity. Does NOT consume a slot —
    /// call <see cref="CommitRateLimitSlot"/> after a successful send.
    /// </summary>
    private bool CheckRateLimitCapacity()
    {
        var now = DateTime.UtcNow;
        lock (_sentAt)
        {
            while (_sentAt.Count > 0 && now - _sentAt.Peek() > TimeSpan.FromHours(1))
            {
                _sentAt.Dequeue();
            }

            return _sentAt.Count < _config.EffectiveMaxMessagesPerHour;
        }
    }

    /// <summary>Records a successful send, consuming one slot in the hourly window.</summary>
    private void CommitRateLimitSlot()
    {
        lock (_sentAt)
        {
            _sentAt.Enqueue(DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the sms.ru response indicates an error — either a top-level
    /// <c>"status":"ERROR"</c> (auth failure, quota exceeded, etc.) or a per-recipient
    /// <c>"sms":{"number":{"status":"ERROR"}}</c> (invalid phone number, etc.).
    /// </summary>
    private static bool LooksLikeSmsRuError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Top-level failure (wrong API key, quota exceeded, …)
            if (root.TryGetProperty("status", out var topStatus) &&
                string.Equals(topStatus.GetString(), "ERROR", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Per-recipient failure: {"sms": {"+7xxx": {"status":"ERROR", "status_code": 212}}}
            if (root.TryGetProperty("sms", out var smsObj) && smsObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var recipient in smsObj.EnumerateObject())
                {
                    if (recipient.Value.TryGetProperty("status", out var recipientStatus) &&
                        string.Equals(recipientStatus.GetString(), "ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
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
