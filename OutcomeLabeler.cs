using _0dtes_app.Models;

namespace _0dtes_agent;

/// <summary>
/// Grades past alerts against what the underlying actually did in the minutes
/// after the signal fired. Writes a LabelRecord per alert that has had enough
/// time to play out (so it can be labeled safely after the market close).
/// </summary>
public sealed class OutcomeLabeler
{
    private static readonly TimeSpan Horizon15 = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Horizon60 = TimeSpan.FromMinutes(60);
    // Label only alerts old enough that the final horizon has passed.
    private static readonly TimeSpan MinAgeBeforeLabel = TimeSpan.FromMinutes(75);

    private static readonly (string Strategy, int Direction)[] DirectionMap =
    [
        ("Bull call", 1),
        ("Bear put", -1),
        ("Price level cross", 0),
        ("Volume surge", 0),
        ("IV spike", 0),
        ("Delta sweet spot", 0),
    ];

    private readonly AlertLogStore _store;
    private readonly YahooReferenceService _yahoo;

    public OutcomeLabeler(AlertLogStore store, YahooReferenceService yahoo)
    {
        _store = store;
        _yahoo = yahoo;
    }

    public async Task<int> LabelAsync(CancellationToken ct = default)
    {
        var alerts = _store.LoadAlerts();
        var existing = _store.LoadLabels();
        var now = DateTime.UtcNow;
        var labeled = 0;

        foreach (var alert in alerts)
        {
            if (existing.ContainsKey(alert.Id))
            {
                continue;
            }
            if (now - alert.SignalTimeUtc < MinAgeBeforeLabel)
            {
                continue;
            }

            var direction = DirectionOf(alert.Strategy);
            var price15 = await _yahoo.GetPriceAtOrAfterAsync(alert.Symbol, alert.SignalTimeUtc, Horizon15, ct);
            await Task.Delay(300, ct).ConfigureAwait(false); // be gentle with Yahoo
            var price60 = await _yahoo.GetPriceAtOrAfterAsync(alert.Symbol, alert.SignalTimeUtc, Horizon60, ct);

            var entry = alert.EntrySpot;
            var ret15 = price15 is { } p15 && entry > 0 ? (p15 - entry) / entry * 100 : (double?)null;
            var ret60 = price60 is { } p60 && entry > 0 ? (p60 - entry) / entry * 100 : (double?)null;

            var label = new LabelRecord(
                alert.Id,
                now,
                entry,
                price15,
                price60,
                ret15,
                ret60,
                DirHit(direction, ret15),
                DirHit(direction, ret60));

            _store.AppendLabel(label);
            existing[alert.Id] = label;
            labeled++;
        }

        return labeled;
    }

    private static bool? DirHit(int direction, double? returnPct)
    {
        if (direction == 0 || returnPct is not { } r)
        {
            return null;
        }
        // A signal "hit" if price moved in the predicted direction by > 0.05%.
        return direction * r > 0.05;
    }

    public static int DirectionOf(string strategy)
    {
        foreach (var (s, d) in DirectionMap)
        {
            if (string.Equals(s, strategy, StringComparison.OrdinalIgnoreCase))
            {
                return d;
            }
        }
        return 0;
    }
}