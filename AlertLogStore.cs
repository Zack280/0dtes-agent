using System.Text.Json;
using System.Text.Json.Serialization;

namespace _0dtes_agent;

/// <summary>
/// One logged alert, with the features we want to learn from.
/// Underlying price moves a few minutes after the signal become the label.
/// </summary>
public sealed record AlertRecord(
    string Id,
    DateTime SignalTimeUtc,
    string Symbol,
    string Strategy,
    int Direction,           // +1 bull / -1 bear / 0 neutral
    double EntrySpot,
    double PrevDayChangePct,
    double PreMarketChangePct,
    int NewsCount,
    double Score,
    string WindowKind,
    bool Sent);              // true if it passed the threshold and was pushed

public sealed record LabelRecord(
    string Id,
    DateTime LabelTimeUtc,
    double? EntrySpot,
    double? Price15,
    double? Price60,
    double? Return15Pct,
    double? Return60Pct,
    bool? DirHit15,          // directional signal correct at +15m
    bool? DirHit60);         // directional signal correct at +60m

/// <summary>
/// Appends alerts and labels to JSONL files under a data directory.
/// Safe for many short-lived processes appending to the same files.
/// </summary>
public sealed class AlertLogStore
{
    private readonly string _alertPath;
    private readonly string _labelPath;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AlertLogStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _alertPath = Path.Combine(dataDir, "alerts.jsonl");
        _labelPath = Path.Combine(dataDir, "labels.jsonl");
    }

    public string AlertPath => _alertPath;
    public string LabelPath => _labelPath;

    public void AppendAlert(AlertRecord alert)
    {
        lock (this)
        {
            File.AppendAllText(_alertPath, JsonSerializer.Serialize(alert, Json) + "\n");
        }
    }

    public List<AlertRecord> LoadAlerts()
    {
        return ReadLines<AlertRecord>(_alertPath);
    }

    public Dictionary<string, LabelRecord> LoadLabels()
    {
        var map = new Dictionary<string, LabelRecord>(StringComparer.Ordinal);
        foreach (var label in ReadLines<LabelRecord>(_labelPath))
        {
            map[label.Id] = label;
        }
        return map;
    }

    public void AppendLabel(LabelRecord label)
    {
        lock (this)
        {
            File.AppendAllText(_labelPath, JsonSerializer.Serialize(label, Json) + "\n");
        }
    }

    private static List<T> ReadLines<T>(string path)
    {
        var list = new List<T>();
        if (!File.Exists(path))
        {
            return list;
        }
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            try
            {
                if (JsonSerializer.Deserialize<T>(line, Json) is { } item)
                {
                    list.Add(item);
                }
            }
            catch
            {
                // Skip malformed lines; they may be from a partial write.
            }
        }
        return list;
    }
}