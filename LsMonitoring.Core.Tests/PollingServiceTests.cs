using LsMonitoring.Core.Models;
using LsMonitoring.Core.Polling;
using LsMonitoring.Core.Sources;

namespace LsMonitoring.Core.Tests;

public sealed class PollingServiceTests
{
    [Fact]
    public async Task PollingServiceUsesNormalizedReadingsContract()
    {
        var source = new FakeSource();
        var service = new PollingService(source, TimeSpan.FromMinutes(1));
        var received = new List<NodeReadings>();

        service.SetNodes([6989]);
        service.ReadingsReady += (_, readings) => received.Add(readings);

        await service.PollOnceAsync();

        Assert.Single(received);
        Assert.Equal(6989, received[0].NodeId);
        Assert.Equal(-1.25, received[0].Latest?.AAxis);
    }

    private sealed class FakeSource : IDataSource
    {
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<NodeInfo>> DiscoverNodesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NodeInfo>>([new NodeInfo(6989, "Fake")]);
        }

        public Task<NodeReadings> FetchReadingsAsync(int nodeId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new NodeReadings
            {
                NodeId = nodeId,
                Model = "Fake",
                Channel = "Ch1",
                Readings =
                [
                    new Reading
                    {
                        Timestamp = new DateTime(2026, 5, 5, 12, 0, 0),
                        AAxis = -1.25,
                        BAxis = 0.5,
                        Temperature = 22.0
                    }
                ]
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
