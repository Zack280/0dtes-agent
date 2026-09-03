namespace _0dtes_agent;

public record SymbolContext(
    string Symbol,
    double? PreviousClose,
    double? PreviousDayChangePercent,
    double? PremarketStart,
    double? PremarketEnd,
    double? PremarketChangePercent,
    IReadOnlyList<NewsItem> News)
{
    public static readonly SymbolContext Empty = new("", null, null, null, null, null, Array.Empty<NewsItem>());
}

public record NewsItem(string Title, string Publisher, string? Link, DateTime Time);

public sealed class MarketBrief
{
    public DateTime GeneratedAt { get; init; } = DateTime.Now;
    public IReadOnlyList<SymbolContext> Symbols { get; init; } = Array.Empty<SymbolContext>();
}

public enum BuyWindowKind
{
    OpenRangeMomentum,
    TrendPullback,
    PostNewsFade,
    PreCloseTheta,
    NoWindow
}

public sealed class BuyWindow
{
    public BuyWindowKind Kind { get; init; } = BuyWindowKind.NoWindow;
    public string Label { get; init; } = "No window";
    public string Details { get; init; } = "";
    public double Score { get; init; }
    public bool IsActionable => Kind != BuyWindowKind.NoWindow && Score >= 50;
}

public sealed class ContractRecommendation
{
    public string Symbol { get; init; } = "";
    public string Strategy { get; init; } = "";
    public string Reason { get; init; } = "";
    public double Price { get; init; }
    public BuyWindow Window { get; set; } = new();
    public string? SuggestedContract { get; init; }
}

/// <summary>
/// Scores a potential trade using market context + the scanner signal, producing a 0-100 value.
/// Higher = more attractive to buy right now.
/// </summary>
public static class RecommendationScorer
{
    public static bool TryScore(
        _0dtes_app.Models.ScanSignal signal,
        SymbolContext ctx,
        _0dtes_app.Models.QuoteTick spot,
        out ContractRecommendation rec,
        DateTime? now = null)
    {
        rec = new ContractRecommendation
        {
            Symbol = signal.Symbol,
            Strategy = signal.Strategy,
            Reason = signal.Reason,
            Price = signal.Price > 0 ? signal.Price : spot.Mid,
        };

        var score = 40.0;

        if (ctx.PremarketChangePercent is { } pre)
        {
            score += Clamp(pre, -3, 3) * 5;
        }

        if (ctx.PreviousDayChangePercent is { } prev)
        {
            score += Clamp(prev, -3, 3) * 2;
        }

        if (ctx.News.Count > 0)
        {
            score += 5;
        }

        var window = BuyWindowEvaluator.Evaluate(spot, ctx, signal, now);
        score += window.Score * 0.25;
        rec.Window = new BuyWindow
        {
            Kind = window.Kind,
            Label = window.Label,
            Details = window.Details,
            Score = Clamp(score, 0, 100),
        };
        return true;
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;
}

/// <summary>
/// Decides which buying window we are in using market context and the scanner signal.
/// </summary>
public static class BuyWindowEvaluator
{
    public static BuyWindow Evaluate(
        _0dtes_app.Models.QuoteTick spot,
        SymbolContext ctx,
        _0dtes_app.Models.ScanSignal signal,
        DateTime? now = null)
    {
        var currentTime = now ?? DateTime.Now;
        var hour = currentTime.Hour + currentTime.Minute / 60.0;

        if (hour < 4 || hour >= 16)
        {
            return new BuyWindow
            {
                Kind = BuyWindowKind.NoWindow,
                Label = "Market closed",
                Details = "Outside regular session; no intraday entry window.",
            };
        }

        var pre = ctx.PremarketChangePercent ?? 0;
        var prev = ctx.PreviousDayChangePercent ?? 0;
        var trending = Math.Abs(pre) >= 0.5 && IsSameSign(pre, signalDirection(signal));
        var momentum = Math.Abs(pre) >= 1.0 && Math.Sign(prev) == Math.Sign(pre);

        switch (signal.Strategy)
        {
            case "Bull call" or "Bear put" when trending:
            case "Volume surge" when trending:
                return new BuyWindow
                {
                    Kind = BuyWindowKind.OpenRangeMomentum,
                    Label = "Open-range momentum",
                    Details = $"Premarket {pre:+0.0;-0.0}% aligns with {signal.Strategy} signal.",
                    Score = 80,
                };

            case "Price level cross" when momentum:
                return new BuyWindow
                {
                    Kind = BuyWindowKind.OpenRangeMomentum,
                    Label = "Level breakout",
                    Details = $"Cross of ${signal.Price:0.00} with momentum (pre {pre:+0.0;-0.0}%).",
                    Score = 85,
                };

            case "IV spike" or null when ctx.News.Count > 0:
                return new BuyWindow
                {
                    Kind = BuyWindowKind.PostNewsFade,
                    Label = "Post-news setup",
                    Details = "News present; watch for a measured move / fade setup.",
                    Score = 55,
                };

            case "Expiry window":
                return new BuyWindow
                {
                    Kind = BuyWindowKind.PreCloseTheta,
                    Label = "Pre-close decay",
                    Details = "Late in expiry; focus on fast theta / close-to-money.",
                    Score = 50,
                };

            default:
                if (Math.Abs(pre) < 0.25)
                {
                    return new BuyWindow
                    {
                        Kind = BuyWindowKind.NoWindow,
                        Label = "Flat premarket",
                        Details = "No directional edge from premarket; avoid forced entries.",
                        Score = 15,
                    };
                }
                return new BuyWindow
                {
                    Kind = BuyWindowKind.TrendPullback,
                    Label = "Trend pullback",
                    Details = "Direction intact; buy pullbacks toward prior session close.",
                    Score = 60,
                };
        }
    }

    private static bool IsSameSign(double a, double b) => Math.Sign(a) == Math.Sign(b);

    private static double signalDirection(_0dtes_app.Models.ScanSignal signal)
        => signal.Strategy switch
        {
            "Bull call" => 1,
            "Bear put" => -1,
            _ => 0,
        };
}
