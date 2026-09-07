using System.Text.Json;
using System.Text.Json.Serialization;

namespace _0dtes_agent;

/// <summary>
/// Persists and reads the seeded market-regime history as regime.jsonl under the
/// data directory. Written daily by the --regime seed job, read at alert time so
/// each alert carries the regime derived from the prior completed session.
/// </summary>
public sealed class RegimeStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public RegimeStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "regime.jsonl");
    }

    public string RegimePath => _path;

    public void WriteAll(IEnumerable<RegimeDay> days)
    {
        var lines = days
            .OrderBy(d => d.Date)
            .Select(d => JsonSerializer.Serialize(d, Json))
            .ToArray();
        File.WriteAllLines(_path, lines);
    }

    /// <summary>Most recent seeded regime row, or null when none is available yet.</summary>
    public RegimeSnapshot? LoadLatest()
    {
        if (!File.Exists(_path))
        {
            return null;
        }
        var last = File.ReadLines(_path).LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (last is null)
        {
            return null;
        }
        try
        {
            var day = JsonSerializer.Deserialize<RegimeDay>(last, Json);
            return day is { Regime: not null } ? new RegimeSnapshot(day.Date, day.Regime) : null;
        }
        catch
        {
            return null;
        }
    }
}