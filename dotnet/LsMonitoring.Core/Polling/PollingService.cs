using LsMonitoring.Core.Models;
using LsMonitoring.Core.Sources;

namespace LsMonitoring.Core.Polling;

public sealed class PollingService : IAsyncDisposable
{
    private readonly IDataSource _source;
    private readonly TimeSpan _interval;
    private readonly List<int> _nodes = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public PollingService(IDataSource source, TimeSpan interval)
    {
        _source = source;
        _interval = interval;
    }

    public event Action<int, NodeReadings>? ReadingsReady;
    public event Action<bool, string>? ConnectionState;
    public event Action<int, string>? Error;

    public void SetNodes(IEnumerable<int> nodeIds)
    {
        lock (_nodes)
        {
            _nodes.Clear();
            _nodes.AddRange(nodeIds.Distinct().Order());
        }
    }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        List<int> nodes;
        lock (_nodes)
        {
            nodes = [.. _nodes];
        }

        if (nodes.Count == 0)
        {
            return;
        }

        var anyOk = false;
        var lastError = "";

        foreach (var nodeId in nodes)
        {
            try
            {
                var readings = await _source.FetchReadingsAsync(nodeId, cancellationToken).ConfigureAwait(false);
                ReadingsReady?.Invoke(nodeId, readings);
                anyOk = true;
            }
            catch (Exception e)
            {
                lastError = e.Message;
                Error?.Invoke(nodeId, e.Message);
            }
        }

        ConnectionState?.Invoke(anyOk, anyOk ? "" : lastError);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _source.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _source.ConnectAsync(cancellationToken).ConfigureAwait(false);
            ConnectionState?.Invoke(true, "");
        }
        catch (Exception e)
        {
            ConnectionState?.Invoke(false, e.Message);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
        }
    }
}
