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
    // Serializes reconfiguration so a new poller is never started before the previous one has
    // fully stopped (two getUpdates loops on one token → Telegram 409 Conflict).
    private readonly SemaphoreSlim _configGate = new(1, 1);
    private TelegramAlertService? _service;
    private string _token = "";
    private string _linkCode = "";

    /// <summary>
    /// Sets/refreshes the bot token, required link code and known chat ids. If the token and link
    /// code are unchanged, the existing poller (and its discovered chat bindings) is kept. The
    /// previous poller is fully stopped and disposed before a new one starts, so two getUpdates
    /// loops never run concurrently against the same token.
    /// </summary>
    public async Task ConfigureAsync(string botToken, string linkCode, IReadOnlyList<long> chatIds)
    {
        botToken = (botToken ?? "").Trim();
        linkCode = (linkCode ?? "").Trim();

        await _configGate.WaitAsync().ConfigureAwait(false);
        try
        {
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
            }

            // Wait for the superseded poller's getUpdates loop to finish (cancel + await) and free
            // its HttpClient/CTS before the replacement poller starts.
            if (previous is not null)
            {
                await DisposeServiceAsync(previous).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(botToken))
            {
                var service = new TelegramAlertService(
                    botToken,
                    chatIds.ToList(),
                    onNewChatIdDiscovered: null,
                    startPolling: true,
                    requiredLinkCode: linkCode);
                lock (_lock)
                {
                    _service = service;
                }
            }
        }
        finally
        {
            _configGate.Release();
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
        TelegramAlertService? service;
        lock (_lock)
        {
            service = _service;
            _service = null;
        }

        // Process is shutting down; fire-and-forget the teardown rather than blocking Dispose.
        if (service is not null)
        {
            _ = Task.Run(() => DisposeServiceAsync(service));
        }

        _configGate.Dispose();
    }

    private static async Task DisposeServiceAsync(TelegramAlertService service)
    {
        try
        {
            // DisposeAsync cancels getUpdates, awaits the polling task, then frees HttpClient/CTS.
            await service.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Teardown of a superseded poller must never crash the host.
        }
    }
}
