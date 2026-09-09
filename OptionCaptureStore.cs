using System.Text.Json;
using System.Text.Json.Serialization;

namespace _0dtes_agent;

/// <summary>
/// A live option quote captured at a labeling horizon (+15m/+60m) while the
/// agent's continuous window is still running. Unlike the recorded entry quote,
/// these are real bids/asks observed minutes after the signal, so the labeler
/// can compute option P&amp;L from actual market data instead of a greeks replay.
/// </summary>
public sealed record OptionCapture(
    string AlertId,
    int HorizonMinutes,      // 15 or 60
    DateTime CaptureTimeUtc,
    string Symbol,
    DateTime ExpiryUtc,
    double Strike,
    string Side,
    double? Bid,
    double? Ask,
    double? Mid,
    double? Delta,
    double? Gamma,
    double? Theta,
    double? Vega,
    double? ImpliedVolatility,
    double? UnderlyingSpot);

/// <summary>
/// Appends live horizon-quote captures to data/captures.jsonl. Safe for many
/// short-lived processes (each window run) appending to the same file.
/// </summary>
public sealed class OptionCaptureStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public OptionCaptureStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "captures.jsonl");
    }

    public string CapturePath => _path;

    public void Append(OptionCapture capture)
    {
        lock (this)
        {
            File.AppendAllText(_path, JsonSerializer.Serialize(capture, Json) + "\n");
        }
    }

    public List<OptionCapture> Load()
    {
        var list = new List<OptionCapture>();
        if (!File.Exists(_path))
        {
            return list;
        }
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            try
            {
                if (JsonSerializer.Deserialize<OptionCapture>(line, Json) is { } item)
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

    /// <summary>Indexes captures by "alertId|horizonMinutes", keeping the newest per key.</summary>
    public Dictionary<string, OptionCapture> LoadByAlertKey()
    {
        return Load()
            .GroupBy(c => $"{c.AlertId}|{c.HorizonMinutes}")
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.CaptureTimeUtc).Last());
    }
}