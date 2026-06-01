using LsMonitoring.Core.Alarms;

namespace LsMonitoring.TelegramBot;

/// <summary>
/// Owns the single <see cref="TelegramAlertService"/> instance for this process. Because exactly
/// one poller runs against the bot token, Telegram never returns 409 Conflict. The HTTP layer
/// (re)configures it and drives alarms; the desktop app stays a thin client and never holds the token.
/// </summary>
public sealed class BotHost : IDisposable
{
    private readonly object _lock = new();
    private TelegramAlertService? _service;
    private string _token = "";
    private string _linkCode = "";

    /// <summary>
    /// Sets/refreshes the bot token, required link code and known chat ids. If the token and link
    /// code are unchanged, the existing poller (and its discovered chat bindings) is kept.
    /// </summary>
    public void Configure(string botToken, string linkCode, IReadOnlyList<long> chatIds)
    {
        botToken = (botToken ?? "").Trim();
        linkCode = (linkCode ?? "").Trim();

        lock (_lock)
        {
            if (_service is not null &&
                string.Equals(_token, botToken, StringComparison.Ordinal) &&
                string.Equals(_linkCode, linkCode, StringComparison.Ordinal))
            {
                return;
            }

            _service?.Stop();
            _service = null;
            _token = botToken;
            _linkCode = linkCode;

            if (!string.IsNullOrWhiteSpace(botToken))
            {
                _service = new TelegramAlertService(
                    botToken,
                    chatIds.ToList(),
                    onNewChatIdDiscovered: null,
                    startPolling: true,
                    requiredLinkCode: linkCode);
            }
        }
    }

    public IReadOnlyList<long> ChatIds
    {
        get
        {
            lock (_lock)
            {
                return _service?.ChatIds ?? Array.Empty<long>();
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_lock)
            {
                return _service?.LastError;
            }
        }
    }

    public bool Configured
    {
        get
        {
            lock (_lock)
            {
                return _service is not null;
            }
        }
    }

    public Task<bool> SendTestAsync()
    {
        TelegramAlertService? service;
        lock (_lock)
        {
            service = _service;
        }

        return service is null ? Task.FromResult(false) : service.SendTestMessageAsync();
    }

    public Task UpdateAlarmAsync(int nodeId, string axis, bool isCritical, double value, DateTime timestamp)
    {
        TelegramAlertService? service;
        lock (_lock)
        {
            service = _service;
        }

        return service is null
            ? Task.CompletedTask
            : service.UpdateAlarmAsync(nodeId, axis, isCritical, value, timestamp);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _service?.Stop();
            _service = null;
        }
    }
}
