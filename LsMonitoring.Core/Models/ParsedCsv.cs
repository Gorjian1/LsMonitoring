namespace LsMonitoring.Core.Models;

public sealed class ParsedCsv
{
    public required Dictionary<string, string> Metadata { get; init; }
    public int? NodeId { get; init; }
    public string? Model { get; init; }
    public string? Channel { get; init; }
    public required List<Reading> Readings { get; init; }

    public Reading? Latest => Readings.Count == 0 ? null : Readings[^1];

    public Reading? LatestValid
    {
        get
        {
            for (var i = Readings.Count - 1; i >= 0; i--)
            {
                if (!Readings[i].Invalid)
                {
                    return Readings[i];
                }
            }

            return null;
        }
    }
}
