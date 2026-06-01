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

        TelegramAlertService? previous;
        lock (_lock)
        {
            if (_service is not null &&
                string.Equals(_token, botToken, StringComparison.Ordinal) &&
                string.Equals(_linkCode, linkCode, StringComparison.Ordinal))
            {
                return;
            }

            previous = _service;
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

        // Dispose the superseded poller (cancels getUpdates, frees its HttpClient + CTS) outside
        // the lock so we never block configuration on the cancelled polling task winding down.
        DisposeQuietly(previous);
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
        TelegramAlertService? service;
        lock (_lock)
        {
            service = _service;
            _service = null;
        }

        DisposeQuietly(service);
    }

    private static void DisposeQuietly(TelegramAlertService? service)
    {
        if (service is null)
        {
            return;
        }

        // Fire-and-forget: DisposeAsync awaits the cancelled polling task before freeing the
        // HttpClient/CTS. We deliberately don't block the caller (Configure/Dispose) on it.
        _ = Task.Run(async () =>
        {
            try
            {
                await service.DisposeAsync();
            }
            catch
            {
                // Teardown of a superseded poller must never crash the host.
            }
        });
    }
}
