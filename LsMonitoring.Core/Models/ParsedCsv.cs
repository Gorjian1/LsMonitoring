namespace LsMonitoring.Core.Models;

public sealed class ParsedCsv
{
    public required Dictionary<string, string> Metadata { get; init; }
    public int? NodeId { get; init; }
    public string? Model { get; init; }
    public string? Channel { get; init; }
    public required List<Reading> Readings { get; init; }

    public Reading? Latest => Readings.Count == 0 ? null : Readings[^1];
}
