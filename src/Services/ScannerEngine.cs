using System.Globalization;
using _0dtes_app.Models;

namespace _0dtes_app.Services;

public sealed class ScannerEngine
{
    private const int MaxPerRulePerScan = 3;

    private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);
    private double? _previousSpot;
    private bool _expiryWindowActive;

    /// <summary>
    /// Pre-seed the de-dup set so that signals already logged in earlier runs
    /// (of a continuous window mode) are not emitted again. Keys use the same
    /// format the scanner produces internally.
    /// </summary>
    public void SeedEmitted(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            _emitted.Add(key);
        }
    }

    /// <summary>
    /// Rebuild the in-memory de-dup key for a signal the same way the scanner
    /// does, so a caller can persist it and seed later runs with it.
    /// </summary>
    public static string KeyFor(ScanSignal signal)
    {
        if (signal.RuleKind == ScanRuleKind.PriceLevelCross)
        {
            // Level-cross keys are "PriceLevelCross|<int level>".
            var level = (long)Math.Floor(signal.Price);
            return $"PriceLevelCross|{level}";
        }
        var side = signal.Side ?? "";
        return $"{signal.RuleKind}|{signal.Strike:0}|{side}";
    }

    public IReadOnlyList<ScanSignal> Evaluate(
        IReadOnlyList<ScanRule> rules,
        IReadOnlyList<OptionChainRow> chain,
        QuoteTick spot,
        DateTime expiryDate,
        DateTime now,
        string? symbol = null)
    {
        var signals = new List<ScanSignal>();
        symbol = string.IsNullOrWhiteSpace(symbol) ? "SPY" : symbol.ToUpperInvariant();

        foreach (var rule in rules)
        {
            if (!rule.IsEnabled)
            {
                continue;
            }
            switch (rule.Kind)
            {
                case ScanRuleKind.DeltaSweetSpot:
                    signals.AddRange(EvaluateDeltaSweetSpot(rule, chain, now, symbol));
                    break;
                case ScanRuleKind.VolumeSurge:
                    signals.AddRange(EvaluateVolumeSurge(rule, chain, now, symbol));
                    break;
                case ScanRuleKind.IvSpike:
                    signals.AddRange(EvaluateIvSpike(rule, chain, spot, now, symbol));
                    break;
                case ScanRuleKind.PriceLevelCross:
                    signals.AddRange(EvaluatePriceLevelCross(rule, spot, now, symbol));
                    break;
                case ScanRuleKind.ExpiryWindow:
                    signals.AddRange(EvaluateExpiryWindow(rule, expiryDate, now, symbol));
                    break;
            }
        }

        return signals;
    }

    private IEnumerable<ScanSignal> EvaluateDeltaSweetSpot(ScanRule rule, IReadOnlyList<OptionChainRow> chain, DateTime now, string symbol)
    {
        var candidates = new List<(double Strike, string Side, OptionQuote Quote)>();
        foreach (var (strike, side, quote) in EnumerateQuotes(chain))
        {
            if (Math.Abs(quote.Delta) is >= 0.20 and <= 0.50)
            {
                candidates.Add((strike, side, quote));
            }
        }

        var result = new List<ScanSignal>();
        foreach (var (strike, side, quote) in candidates
                     .OrderByDescending(c => Math.Abs(c.Quote.Delta))
                     .Take(MaxPerRulePerScan))
        {
            if (!_emitted.Add(Key(rule, strike, side)))
            {
                continue;
            }
            result.Add(new ScanSignal(
                symbol,
                side == "C" ? "Bull call" : "Bear put",
                $"Δ {quote.Delta:+0.00;-0.00} · {strike:0} strike · IV {quote.ImpliedVolatility:P0}",
                quote.Mid,
                now,
                rule.Kind,
                strike,
                side,
                quote));
        }
        return result;
    }

    private IEnumerable<ScanSignal> EvaluateVolumeSurge(ScanRule rule, IReadOnlyList<OptionChainRow> chain, DateTime now, string symbol)
    {
        var quotes = EnumerateQuotes(chain).Select(c => c.Quote).ToArray();
        if (quotes.Length == 0)
        {
            return Array.Empty<ScanSignal>();
        }
        var sorted = quotes.Select(q => (double)q.Volume).OrderBy(v => v).ToArray();
        var median = sorted[sorted.Length / 2];
        if (median <= 0)
        {
            return Array.Empty<ScanSignal>();
        }

        var candidates = EnumerateQuotes(chain)
            .Where(c => c.Quote.Volume > 2 * median)
            .OrderByDescending(c => c.Quote.Volume)
            .Take(MaxPerRulePerScan)
            .ToArray();

        var result = new List<ScanSignal>();
        foreach (var (strike, side, quote) in candidates)
        {
            if (!_emitted.Add(Key(rule, strike, side)))
            {
                continue;
            }
            result.Add(new ScanSignal(
                symbol,
                "Volume surge",
                $"{side} {strike:0} · {quote.Volume:N0} vol · {quote.Volume / median:F1}x median",
                quote.Mid,
                now,
                rule.Kind,
                strike,
                side,
                quote));
        }
        return result;
    }

    private IEnumerable<ScanSignal> EvaluateIvSpike(ScanRule rule, IReadOnlyList<OptionChainRow> chain, QuoteTick spot, DateTime now, string symbol)
    {
        var atmRow = chain
            .OrderBy(r => Math.Abs(r.Strike - spot.Mid))
            .FirstOrDefault();
        var atmIv = (atmRow?.Call?.ImpliedVolatility ?? 0) > 0
            ? atmRow!.Call!.ImpliedVolatility
            : atmRow?.Put?.ImpliedVolatility ?? 0;
        if (atmIv <= 0)
        {
            return Array.Empty<ScanSignal>();
        }

        // Only tradeable, near-money contracts: real two-sided prices, a sane
        // delta (deep-ITM anchors get garbage IV from the feed and are pure
        // delta exposure, not a volatility play), and an IV that is clearly
        // elevated but still within a plausible range for a real spike.
        var candidates = EnumerateQuotes(chain)
            .Where(c => c.Quote.Bid > 0 && c.Quote.Ask > 0)
            .Where(c => Math.Abs(c.Quote.Delta) is >= 0.10 and <= 0.90)
            .Where(c => c.Quote.ImpliedVolatility > 1.5 * atmIv && c.Quote.ImpliedVolatility <= 2.0)
            .OrderByDescending(c => c.Quote.ImpliedVolatility)
            .Take(MaxPerRulePerScan)
            .ToArray();

        var result = new List<ScanSignal>();
        foreach (var (strike, side, quote) in candidates)
        {
            if (!_emitted.Add(Key(rule, strike, side)))
            {
                continue;
            }
            result.Add(new ScanSignal(
                symbol,
                "IV spike",
                $"{side} {strike:0} · IV {quote.ImpliedVolatility:P0} · {quote.ImpliedVolatility / atmIv:F1}x ATM",
                quote.Mid,
                now,
                rule.Kind,
                strike,
                side,
                quote));
        }
        return result;
    }

    private IEnumerable<ScanSignal> EvaluatePriceLevelCross(ScanRule rule, QuoteTick spot, DateTime now, string symbol)
    {
        var result = new List<ScanSignal>();
        if (_previousSpot is { } previous)
        {
            var crossed = FindCrossedLevel(previous, spot.Last);
            if (crossed is { } level && _emitted.Add(Key(rule, level)))
            {
                result.Add(new ScanSignal(
                    symbol,
                    "Level cross",
                    $"{symbol} crossed ${level.ToString("0.00", CultureInfo.InvariantCulture)}",
                    spot.Last,
                    now,
                    rule.Kind));
            }
        }
        _previousSpot = spot.Last;
        return result;
    }

    private IEnumerable<ScanSignal> EvaluateExpiryWindow(ScanRule rule, DateTime expiryDate, DateTime now, string symbol)
    {
        var close = expiryDate.Date.AddHours(16);
        var hoursLeft = (close - now).TotalHours;
        var inWindow = expiryDate.Date == now.Date && hoursLeft is > 0 and <= 2;
        if (inWindow && !_expiryWindowActive && _emitted.Add(Key(rule, "enter")))
        {
            _expiryWindowActive = true;
            return new[]
            {
                new ScanSignal(symbol, "Expiry window", $"{hoursLeft:F1}h to {close:HH:mm} expiration", 0, now, rule.Kind)
            };
        }
        if (!inWindow)
        {
            _expiryWindowActive = false;
        }
        return Array.Empty<ScanSignal>();
    }

    private static double? FindCrossedLevel(double previous, double current)
    {
        var floorPrevious = Math.Floor(previous);
        var floorCurrent = Math.Floor(current);
        if (Math.Abs(floorCurrent - floorPrevious) < 1)
        {
            return null;
        }
        return Math.Max(floorPrevious, floorCurrent);
    }

    private static IEnumerable<(double Strike, string Side, OptionQuote Quote)> EnumerateQuotes(IReadOnlyList<OptionChainRow> chain)
    {
        foreach (var row in chain)
        {
            if (row.Call is { } call)
            {
                yield return (row.Strike, "C", call);
            }
            if (row.Put is { } put)
            {
                yield return (row.Strike, "P", put);
            }
        }
    }

    private static string Key(ScanRule rule, params object[] parts)
        => string.Join("|", parts.Prepend((object)rule.Kind.ToString()));
}
