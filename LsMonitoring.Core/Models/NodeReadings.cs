namespace LsMonitoring.Core.Models;

public sealed class NodeReadings
{
    public required int NodeId { get; init; }
    public string? Model { get; init; }
    public string? Channel { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public required IReadOnlyList<Reading> Readings { get; init; }
    public DateTime ReceivedAt { get; init; } = DateTime.Now;

    public Reading? Latest => Readings.Count == 0 ? null : Readings[^1];

    public static NodeReadings FromParsedCsv(int requestedNodeId, ParsedCsv parsed)
    {
        return new NodeReadings
        {
            NodeId = parsed.NodeId ?? requestedNodeId,
            Model = parsed.Model,
            Channel = parsed.Channel,
            Metadata = parsed.Metadata,
            Readings = parsed.Readings
        };
    }
}
