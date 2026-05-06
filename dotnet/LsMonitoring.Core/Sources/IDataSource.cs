using LsMonitoring.Core.Models;

namespace LsMonitoring.Core.Sources;

public interface IDataSource : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NodeInfo>> DiscoverNodesAsync(CancellationToken cancellationToken = default);
    Task<NodeReadings> FetchReadingsAsync(int nodeId, CancellationToken cancellationToken = default);
}
