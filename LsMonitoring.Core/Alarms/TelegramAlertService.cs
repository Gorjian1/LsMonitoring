using System.Text;
using System.Text.Json;
using LsMonitoring.Core.IO;

namespace LsMonitoring.Core.Alarms;

public class ActiveTelegramAlarm
{
    public DateTime StartTime { get; set; }
    public int MessageId { get; set; }
    public double LastValue { get; set; }
}

public class TelegramAlertService : IUpdatableAlertChannel, IAsyncDisposable
{
    private static readonly string LogFilePath = AppPaths.LogFile("telegram_debug.txt");

    private readonly string _botToken;
    private readonly List<long> _chatIds;
    private readonly string _requiredLinkCode;
    private readonly object _chatIdsLock = new();
    private readonly HttpClient _httpClient;
    private readonly Action<long>? _onNewChatIdDiscovered;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollingTask;
    private long _lastUpdateId = 0;

    private readonly Dictionary<(int NodeId, string Axis), Dictionary<long, ActiveTelegramAlarm>> _activeAlarms = new();

    public string? LastError { get; private set; }
    public string ChannelName => "Telegram";

    /// <summary>Thread-safe snapshot of the currently bound chat ids.</summary>
    public IReadOnlyList<long> ChatIds => GetChatIdsSnapshot();

    public TelegramAlertService(
        string botToken,
        List<long> chatIds,
        Action<long>? onNewChatIdDiscovered = null,
        bool startPolling = true,
        string? requiredLinkCode = null)
    {
        _botToken = botToken.Trim();
        _chatIds = chatIds;
        _requiredLinkCode = (requiredLinkCode ?? "").Trim();
        _onNewChatIdDiscovered = onNewChatIdDiscovered;
        _httpClient = new HttpClient();

        if (startPolling)
        {
            StartPolling();
        }
    }

    public void StartPolling()
    {
        _pollingTask ??= PollUpdatesAsync(_cts.Token);
    }

    public void Stop()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
    }

    public async Task StopAsync()
    {
        Stop();
        if (_pollingTask is null)
        {
            return;
        }

        try
        {
            await _pollingTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _httpClient.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task PollUpdatesAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await FetchUpdatesOnceAsync(TimeSpan.FromSeconds(5), token);
                await Task.Delay(2000, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) 
            {
                RecordError($"getUpdates exception: {ex.Message}");
                try
                {
                    await Task.Delay(2000, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    public async Task<int> DiscoverChatIdsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var knownBeforeDiscovery = GetChatIdsSnapshot().ToHashSet();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        while (!linkedCts.Token.IsCancellationRequested)
        {
            try
            {
                await FetchUpdatesOnceAsync(TimeSpan.FromSeconds(10), linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var discovered = CountNewChatIds(knownBeforeDiscovery);
            if (discovered > 0)
            {
                return discovered;
            }

            try
            {
                await Task.Delay(1000, linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        return CountNewChatIds(knownBeforeDiscovery);
    }

    public async Task<bool> SendTestMessageAsync()
    {
        LastError = null;
        var chatIds = GetChatIdsSnapshot();
        if (chatIds.Count == 0)
        {
            LastError = "Не зарегистрировано ни одного Telegram-чата.";
            return false;
        }

        bool allSuccess = true;
        foreach (var chatId in chatIds)
        {
            var msgId = await SendMessageAsync(chatId, "🛠 Тестовое сообщение от LS Monitoring!");
            if (msgId == null)
                allSuccess = false;
        }
        return allSuccess;
    }

    public Task<bool> SendTestAsync()
    {
        return SendTestMessageAsync();
    }

    public Task NotifyStartedAsync(AlertEvent alertEvent)
    {
        return UpdateAlarmAsync(alertEvent.NodeId, alertEvent.Axis, true, alertEvent.CurrentValue, alertEvent.StartedAt);
    }

    public Task NotifyActiveAsync(AlertEvent alertEvent)
    {
        return UpdateAlarmAsync(alertEvent.NodeId, alertEvent.Axis, true, alertEvent.CurrentValue, alertEvent.UpdatedAt);
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
        var key = (nodeId, axis);
        var chatCount = GetChatIdsSnapshot().Count;
        var hasKey = _activeAlarms.ContainsKey(key);
        LogDiagnostic($"UpdateAlarmAsync node={nodeId} axis={axis} critical={isCritical} value={value:F3} chats={chatCount} hasActiveKey={hasKey}");

        if (chatCount == 0)
        {
            LogDiagnostic($"UpdateAlarmAsync skipped: no chat ids registered");
            return;
        }

        if (isCritical)
        {
            if (!_activeAlarms.ContainsKey(key))
            {
                var activeMap = new Dictionary<long, ActiveTelegramAlarm>();

                foreach (var chatId in GetChatIdsSnapshot())
                {
                    var msg = FormatMessage(nodeId, axis, value, timestamp, timestamp);
                    var msgId = await SendMessageAsync(chatId, msg);
                    if (msgId != null)
                    {
                        activeMap[chatId] = new ActiveTelegramAlarm
                        {
                            StartTime = timestamp,
                            MessageId = msgId.Value,
                            LastValue = value
                        };
                    }
                }

                if (activeMap.Count > 0)
                {
                    _activeAlarms[key] = activeMap;
                }
            }
            else
            {
                var activeMap = _activeAlarms[key];
                foreach (var kvp in activeMap)
                {
                    var chatId = kvp.Key;
                    var alarm = kvp.Value;
                    alarm.LastValue = value;
                    
                    var msg = FormatMessage(nodeId, axis, alarm.LastValue, alarm.StartTime, timestamp);
                    await EditMessageAsync(chatId, alarm.MessageId, msg);
                }
            }
        }
        else
        {
            if (_activeAlarms.TryGetValue(key, out var activeMap))
            {
                foreach (var kvp in activeMap)
                {
                    var chatId = kvp.Key;
                    var alarm = kvp.Value;
                    var msg = FormatResolvedMessage(nodeId, axis, alarm.StartTime, timestamp);
                    await EditMessageAsync(chatId, alarm.MessageId, msg);
                }
                _activeAlarms.Remove(key);
            }
        }
    }

    private string FormatMessage(int nodeId, string axis, double value, DateTime startTime, DateTime currentTime)
    {
        var duration = currentTime - startTime;
        return $"🔔 LS Monitoring\nУзел {nodeId}, ось {axis}: отклонение вышло за заданный максимум.\nОтклонение: {value:F3}°\nНачало: {startTime:HH:mm:ss}\nДлительность: {duration.ToString(@"hh\:mm\:ss")}";
    }

    private string FormatResolvedMessage(int nodeId, string axis, DateTime startTime, DateTime resolveTime)
    {
        var duration = resolveTime - startTime;
        return $"✅ LS Monitoring\nУзел {nodeId}, ось {axis}: значение вернулось в норму.\nНачало события: {startTime:HH:mm:ss}\nЗавершено: {resolveTime:HH:mm:ss}\nДлительность: {duration.ToString(@"hh\:mm\:ss")}";
    }

    private async Task<int?> SendMessageAsync(long chatId, string text)
    {
        try
        {
            var content = new StringContent(JsonSerializer.Serialize(new { chat_id = chatId, text = text }), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(ApiPath("sendMessage"), content);
            var body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                var doc = JsonDocument.Parse(body);
                return doc.RootElement.GetProperty("result").GetProperty("message_id").GetInt32();
            }

            RecordError($"sendMessage HTTP {(int)response.StatusCode} {response.StatusCode}: {Truncate(body)}");
        }
        catch (Exception ex)
        {
            RecordError($"sendMessage exception: {ex.Message}");
        }
        return null;
    }

    private async Task EditMessageAsync(long chatId, int messageId, string text)
    {
        try
        {
            var content = new StringContent(JsonSerializer.Serialize(new { chat_id = chatId, message_id = messageId, text = text }), Encoding.UTF8, "application/json");
            await _httpClient.PostAsync(ApiPath("editMessageText"), content);
        }
        catch (Exception ex)
        {
            RecordError($"editMessageText exception: {ex.Message}");
        }
    }

    private async Task FetchUpdatesOnceAsync(TimeSpan timeout, CancellationToken token)
    {
        var timeoutSeconds = Math.Max(0, (int)Math.Ceiling(timeout.TotalSeconds));
        var url = _lastUpdateId == 0
            ? $"{ApiPath("getUpdates")}?timeout={timeoutSeconds}"
            : $"{ApiPath("getUpdates")}?offset={_lastUpdateId + 1}&timeout={timeoutSeconds}";

        var response = await _httpClient.GetAsync(url, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            RecordError($"getUpdates HTTP {(int)response.StatusCode} {response.StatusCode}: {Truncate(body)}");
            return;
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("result", out var results))
        {
            foreach (var update in results.EnumerateArray())
            {
                ProcessUpdate(update);
            }
        }
    }

    private void ProcessUpdate(JsonElement update)
    {
        if (update.TryGetProperty("update_id", out var updateIdProp))
        {
            _lastUpdateId = updateIdProp.GetInt64();
        }

        if (!update.TryGetProperty("message", out var msg) ||
            !msg.TryGetProperty("chat", out var chat) ||
            !chat.TryGetProperty("id", out var chatIdProp))
        {
            return;
        }

        var chatId = chatIdProp.GetInt64();
        var text = msg.TryGetProperty("text", out var textProp) ? textProp.GetString() : "";
        var linkCodeMatches = LinkCodeMatches(text);

        if (!linkCodeMatches && !string.IsNullOrWhiteSpace(_requiredLinkCode))
        {
            if (IsStartCommand(text))
            {
                _ = SendMessageAsync(chatId, "Код привязки не совпал. Откройте ссылку или QR из LS Monitoring и отправьте /start с кодом.");
            }

            return;
        }

        if (linkCodeMatches && TryAddChatId(chatId))
        {
            _ = SendMessageAsync(chatId, "👋 Вы успешно подписаны на уведомления LS Monitoring!");
            _onNewChatIdDiscovered?.Invoke(chatId);
        }
        else if (linkCodeMatches)
        {
            _ = SendMessageAsync(chatId, "✅ Вы уже подписаны на уведомления. Ожидайте тревожных сигналов от LS Monitoring!");
        }
    }

    private bool LinkCodeMatches(string? text)
    {
        if (!IsStartCommand(text))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_requiredLinkCode))
        {
            return true;
        }

        var payload = ExtractStartPayload(text);
        return string.Equals(payload, _requiredLinkCode, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStartCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Trim().StartsWith("/start", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractStartPayload(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var trimmed = text.Trim();
        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? parts[1].Trim() : "";
    }

    private IReadOnlyList<long> GetChatIdsSnapshot()
    {
        lock (_chatIdsLock)
        {
            return _chatIds.ToList();
        }
    }

    private bool TryAddChatId(long chatId)
    {
        lock (_chatIdsLock)
        {
            if (_chatIds.Contains(chatId))
            {
                return false;
            }

            _chatIds.Add(chatId);
            return true;
        }
    }

    private int CountNewChatIds(HashSet<long> knownBeforeDiscovery)
    {
        lock (_chatIdsLock)
        {
            return _chatIds.Count(chatId => !knownBeforeDiscovery.Contains(chatId));
        }
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
            // Диагностика не должна ломать доставку тревог.
        }
    }

    private static string Truncate(string value)
    {
        const int maxLength = 500;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private string ApiPath(string method)
    {
        return $"https://api.telegram.org/bot{_botToken}/{method}";
    }
}
