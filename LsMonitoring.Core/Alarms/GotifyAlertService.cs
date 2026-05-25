using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LsMonitoring.Core.Configuration;

namespace LsMonitoring.Core.Alarms;

public static class GotifyAlertExtras
{
    public const string AlarmKey = "lsmonitoring::alarm";
    public const string TestKey = "lsmonitoring::test";
}

public sealed class ActiveGotifyAlarm
{
    public DateTime StartTime { get; set; }
    public double LastValue { get; set; }
}

public sealed class GotifyAlertService
{
    private const string LogFilePath = "logs/gotify_debug.txt";

    private readonly PushConfig _config;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<(int NodeId, string Axis), ActiveGotifyAlarm> _activeAlarms = [];

    public string? LastError { get; private set; }

    public GotifyAlertService(PushConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<bool> SendTestMessageAsync()
    {
        return await SendMessageAsync(
            "LS Monitoring",
            "Тестовый push от LS Monitoring. Если уведомление пришло, Gotify подключён.",
            _config.Priority,
            BuildTestExtras());
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

            var sent = await SendMessageAsync(
                $"Тревога: узел {nodeId}, ось {axis}",
                FormatAlarmMessage(nodeId, axis, value, timestamp),
                _config.Priority,
                BuildActiveAlarmExtras(nodeId, axis, value, timestamp, timestamp));

            if (sent)
            {
                _activeAlarms[key] = new ActiveGotifyAlarm
                {
                    StartTime = timestamp,
                    LastValue = value
                };
            }

            return;
        }

        if (_activeAlarms.TryGetValue(key, out var alarm))
        {
            var sent = await SendMessageAsync(
                $"Норма: узел {nodeId}, ось {axis}",
                FormatResolvedMessage(nodeId, axis, alarm.StartTime, timestamp),
                _config.Priority,
                BuildResolvedAlarmExtras(nodeId, axis, alarm.LastValue, alarm.StartTime, timestamp));

            if (sent)
            {
                _activeAlarms.Remove(key);
            }
        }
    }

    private async Task<bool> SendMessageAsync(
        string title,
        string message,
        int priority,
        IReadOnlyDictionary<string, object?>? extras = null)
    {
        LastError = null;

        if (!_config.HasDeliverySettings)
        {
            RecordError("Не задан Gotify: нужны URL сервера и app token.");
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.EffectiveServerUrl}/message");
            request.Headers.TryAddWithoutValidation("X-Gotify-Key", _config.AppToken.Trim());

            var payload = new GotifyMessage(
                title,
                message,
                Math.Clamp(priority, 0, 10),
                extras);

            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                LogDiagnostic($"gotify-sent url={_config.EffectiveServerUrl} title={title}");
                return true;
            }

            RecordError($"Gotify HTTP {(int)response.StatusCode} {response.StatusCode}: {Truncate(body)}");
            return false;
        }
        catch (Exception ex)
        {
            RecordError($"Gotify exception: {ex.Message}");
            return false;
        }
    }

    private static string FormatAlarmMessage(int nodeId, string axis, double value, DateTime startTime)
    {
        return
            $"Узел {nodeId}, ось {axis}: отклонение вышло за заданный максимум.\n" +
            $"Отклонение: {value:F3}°\n" +
            $"Начало: {startTime:dd.MM.yyyy HH:mm:ss}";
    }

    private static string FormatResolvedMessage(int nodeId, string axis, DateTime startTime, DateTime resolveTime)
    {
        var duration = resolveTime - startTime;
        return
            $"Узел {nodeId}, ось {axis}: значение вернулось в норму.\n" +
            $"Начало: {startTime:dd.MM.yyyy HH:mm:ss}\n" +
            $"Завершено: {resolveTime:dd.MM.yyyy HH:mm:ss}\n" +
            $"Длительность: {duration:hh\\:mm\\:ss}";
    }

    private static IReadOnlyDictionary<string, object?> BuildTestExtras()
    {
        return new Dictionary<string, object?>
        {
            [GotifyAlertExtras.TestKey] = new GotifyTestEnvelope("test", DateTimeOffset.Now)
        };
    }

    private static IReadOnlyDictionary<string, object?> BuildActiveAlarmExtras(
        int nodeId,
        string axis,
        double value,
        DateTime startedAt,
        DateTime updatedAt)
    {
        return new Dictionary<string, object?>
        {
            [GotifyAlertExtras.AlarmKey] = new GotifyAlarmEnvelope(
                "active",
                nodeId,
                axis,
                value,
                ToDateTimeOffset(startedAt),
                ToDateTimeOffset(updatedAt),
                null,
                null)
        };
    }

    private static IReadOnlyDictionary<string, object?> BuildResolvedAlarmExtras(
        int nodeId,
        string axis,
        double value,
        DateTime startedAt,
        DateTime resolvedAt)
    {
        var duration = resolvedAt - startedAt;
        return new Dictionary<string, object?>
        {
            [GotifyAlertExtras.AlarmKey] = new GotifyAlarmEnvelope(
                "resolved",
                nodeId,
                axis,
                value,
                ToDateTimeOffset(startedAt),
                ToDateTimeOffset(resolvedAt),
                ToDateTimeOffset(resolvedAt),
                Math.Max(0, (int)Math.Round(duration.TotalSeconds)))
        };
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime value)
    {
        if (value.Kind == DateTimeKind.Utc)
        {
            return new DateTimeOffset(value);
        }

        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local));
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

    private sealed record GotifyMessage(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("priority")] int Priority,
        [property: JsonPropertyName("extras")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyDictionary<string, object?>? Extras);

    private sealed record GotifyAlarmEnvelope(
        [property: JsonPropertyName("event")] string Event,
        [property: JsonPropertyName("nodeId")] int NodeId,
        [property: JsonPropertyName("axis")] string Axis,
        [property: JsonPropertyName("value")] double Value,
        [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
        [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
        [property: JsonPropertyName("resolvedAt")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        DateTimeOffset? ResolvedAt,
        [property: JsonPropertyName("durationSeconds")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? DurationSeconds);

    private sealed record GotifyTestEnvelope(
        [property: JsonPropertyName("event")] string Event,
        [property: JsonPropertyName("sentAt")] DateTimeOffset SentAt);
}
