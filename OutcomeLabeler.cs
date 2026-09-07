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

            var opt15 = ProjectOptionReturn(alert, price15, Horizon15);
            var opt60 = ProjectOptionReturn(alert, price60, Horizon60);

            var label = new LabelRecord(
                alert.Id,
                now,
                entry,
                price15,
                price60,
                ret15,
                ret60,
                DirHit(direction, ret15),
                DirHit(direction, ret60),
                opt15,
                opt60);

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

    /// <summary>
    /// Replays the recorded greeks snapshot against the underlying's actual
    /// path after the signal, returning the option's projected P&amp;L as a
    /// percent of entry debit. Buy at the recorded ask, sell at the exit
    /// mid minus the recorded half-spread. IV is held flat between horizons.
    /// Returns null when no contract snapshot or quote is available.
    /// </summary>
    private static double? ProjectOptionReturn(AlertRecord alert, double? underlyingAt, TimeSpan horizon)
    {
        var c = alert.Contract;
        if (c is null ||
            underlyingAt is not { } spotAt ||
            c.EntryAsk is not { } ask || ask <= 0 ||
            c.EntryBid is not { } bid || bid < 0)
        {
            return null;
        }

        var entryMid = c.EntryMid ?? (bid + ask) / 2;
        var halfSpread = ask > bid ? (ask - bid) / 2 : 0;

        var dS = spotAt - alert.EntrySpot;
        var dT = horizon.TotalMinutes / 1440.0; // theta is per-day
        var dPriced = (c.Delta ?? 0) * dS
                    + 0.5 * (c.Gamma ?? 0) * dS * dS
                    + (c.Theta ?? 0) * dT;

        var exitBid = entryMid + dPriced - halfSpread;
        return (exitBid - ask) / ask * 100;
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