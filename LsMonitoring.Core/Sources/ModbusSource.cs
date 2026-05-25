using LsMonitoring.Core.Models;

namespace LsMonitoring.Core.Sources;

public sealed class ModbusSource : IDataSource
{
    public ModbusSource(string gatewayIp, int port = 502, IReadOnlyList<int>? unitIds = null)
    {
        GatewayIp = gatewayIp;
        Port = port;
        UnitIds = unitIds ?? [];
    }

    public string GatewayIp { get; }
    public int Port { get; }
    public IReadOnlyList<int> UnitIds { get; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Modbus source pending: need official Worldsensing register map.");
    }

    public Task<IReadOnlyList<NodeInfo>> DiscoverNodesAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Modbus discovery pending: need official Worldsensing register map.");
    }

    public Task<NodeReadings> FetchReadingsAsync(int nodeId, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Modbus readings pending: need official Worldsensing register map.");
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
