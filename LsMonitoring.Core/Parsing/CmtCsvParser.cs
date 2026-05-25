using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LsMonitoring.Core.Models;

namespace LsMonitoring.Core.Parsing;

public static partial class CmtCsvParser
{
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss"
    ];

    public static ParsedCsv Parse(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '\ufeff')
        {
            text = text[1..];
        }

        return Parse(text);
    }

    public static ParsedCsv ParseFile(string path)
    {
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    public static ParsedCsv Parse(string text)
    {
        var allRows = ReadRows(text).ToList();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headerIndex = -1;

        for (var i = 0; i < allRows.Count; i++)
        {
            var row = allRows[i];
            if (row.Count == 0)
            {
                continue;
            }

            var first = Clean(row[0]).ToLowerInvariant();
            if (first == "date-and-time")
            {
                headerIndex = i;
                break;
            }

            if (row.Count >= 2)
            {
                metadata[Clean(row[0])] = Clean(row[1]);
            }
        }

        if (headerIndex < 0)
        {
            throw new InvalidDataException("CSV header (Date-and-time) not found.");
        }

        var (mapping, channel) = ClassifyHeader(allRows[headerIndex]);
        if (!mapping.ContainsKey("ts"))
        {
            throw new InvalidDataException("Date-and-time column not found in header.");
        }

        var readings = new List<Reading>();
        foreach (var row in allRows.Skip(headerIndex + 1))
        {
            if (row.Count == 0 || row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var tsIndex = mapping["ts"];
            if (tsIndex >= row.Count || !TryParseTimestamp(row[tsIndex], out var timestamp))
            {
                continue;
            }

            var reading = new Reading
            {
                Timestamp = timestamp,
                Temperature = Cell(row, mapping, "temp"),
                AAxis = Cell(row, mapping, "a_axis"),
                BAxis = Cell(row, mapping, "b_axis"),
                AVariation = Cell(row, mapping, "a_var"),
                BVariation = Cell(row, mapping, "b_var")
            };
            reading.MarkInvalidIfOutOfRange();
            readings.Add(reading);
        }

        var nodeRaw = metadata.TryGetValue("Node ID", out var nodeId)
            ? nodeId
            : metadata.GetValueOrDefault("NodeID");

        return new ParsedCsv
        {
            Metadata = metadata,
            NodeId = int.TryParse(nodeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nid) ? nid : null,
            Model = metadata.GetValueOrDefault("Model"),
            Channel = channel,
            Readings = readings
        };
    }

    public static double? EstimateSamplingIntervalSeconds(IEnumerable<Reading> readings)
    {
        var ordered = readings.ToList();
        if (ordered.Count < 2)
        {
            return null;
        }

        var deltas = ordered.Zip(ordered.Skip(1), (a, b) => (b.Timestamp - a.Timestamp).TotalSeconds)
            .Where(x => x > 0)
            .Order()
            .ToList();

        return deltas.Count == 0 ? null : deltas[deltas.Count / 2];
    }

    private static (Dictionary<string, int> Mapping, string? Channel) ClassifyHeader(IReadOnlyList<string> columns)
    {
        var mapping = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? channel = null;

        for (var i = 0; i < columns.Count; i++)
        {
            var name = Clean(columns[i]);
            if (name.Length == 0)
            {
                continue;
            }

            if (name.Equals("Date-and-time", StringComparison.OrdinalIgnoreCase))
            {
                mapping["ts"] = i;
                continue;
            }

            foreach (var (field, regex) in ColumnPatterns())
            {
                var match = regex.Match(name);
                if (!match.Success)
                {
                    continue;
                }

                mapping[field] = i;
                channel = match.Groups[2].Value;
                break;
            }
        }

        return (mapping, channel);
    }

    private static double? Cell(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> mapping, string field)
    {
        if (!mapping.TryGetValue(field, out var index) || index >= row.Count)
        {
            return null;
        }

        var text = Clean(row[index]);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static bool TryParseTimestamp(string value, out DateTime timestamp)
    {
        return DateTime.TryParseExact(Clean(value), TimestampFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out timestamp);
    }

    private static string Clean(string value)
    {
        return value.Trim().Trim('"');
    }

    private static IEnumerable<List<string>> ReadRows(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return ParseCsvLine(line);
        }
    }

    private static List<string> ParseCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                cells.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        cells.Add(current.ToString());
        return cells;
    }

    private static IReadOnlyList<(string Field, Regex Regex)> ColumnPatterns() =>
    [
        ("temp", TempColumnRegex()),
        ("a_axis", AAxisColumnRegex()),
        ("b_axis", BAxisColumnRegex()),
        ("a_var", AVariationColumnRegex()),
        ("b_var", BVariationColumnRegex())
    ];

    [GeneratedRegex("^Temp-(\\d+)-(Ch\\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TempColumnRegex();

    [GeneratedRegex("^Aaxis-(\\d+)-(Ch\\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AAxisColumnRegex();

    [GeneratedRegex("^Baxis-(\\d+)-(Ch\\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex BAxisColumnRegex();

    [GeneratedRegex("^AaxisVariation-(\\d+)-(Ch\\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AVariationColumnRegex();

    [GeneratedRegex("^BaxisVariation-(\\d+)-(Ch\\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex BVariationColumnRegex();
}
