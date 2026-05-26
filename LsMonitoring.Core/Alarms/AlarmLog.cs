using System.Globalization;
using LsMonitoring.Core.Models;

namespace LsMonitoring.Core.Alarms;

public sealed class AlarmLog
{
    private readonly string _path;

    public AlarmLog(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (!File.Exists(path))
        {
            File.WriteAllText(path, "logged_at,node_id,ts,status,a,b,reasons" + Environment.NewLine);
        }
    }

    public void Write(int nodeId, Reading reading, Evaluation evaluation)
    {
        var row = string.Join(",",
            Escape(DateTime.Now.ToString("s", CultureInfo.InvariantCulture)),
            nodeId.ToString(CultureInfo.InvariantCulture),
            Escape(reading.Timestamp.ToString("s", CultureInfo.InvariantCulture)),
            evaluation.Status,
            evaluation.AValue?.ToString(CultureInfo.InvariantCulture) ?? "",
            evaluation.BValue?.ToString(CultureInfo.InvariantCulture) ?? "",
            Escape(string.Join("; ", evaluation.Reasons)));

        File.AppendAllText(_path, row + Environment.NewLine);
    }

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
