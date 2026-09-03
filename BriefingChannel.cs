using System.Globalization;
using System.Text;
using _0dtes_app.Models;

namespace _0dtes_agent;

/// <summary>
/// Formats market context into a readable brief and pushes it to ntfy.
/// </summary>
public sealed class BriefingChannel
{
    private readonly NtfyNotifier _notifier;
    private readonly YahooReferenceService _yahoo;

    public BriefingChannel(NtfyNotifier notifier, YahooReferenceService yahoo)
    {
        _notifier = notifier;
        _yahoo = yahoo;
    }

    public async Task<bool> SendBriefAsync(
        MarketBrief brief,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"0DTE Brief · {brief.GeneratedAt:ddd MMM d, HH:mm}");
        foreach (var ctx in brief.Symbols)
        {
            AppendSymbol(sb, ctx);
        }
        return await _notifier.SendAsync(
            "0DTE market brief",
            sb.ToString(),
            new[] { "chart_with_upwards_trend" },
            ct);
    }

    public async Task<bool> SendBriefAsync(
        IEnumerable<string> symbols,
        CancellationToken ct = default)
    {
        var ctxs = new List<SymbolContext>();
        foreach (var symbol in symbols)
        {
            var ctx = await _yahoo.GetSymbolContextAsync(symbol, 3, ct);
            if (ctx is not null)
            {
                ctxs.Add(ctx);
            }
        }
        return await SendBriefAsync(new MarketBrief { Symbols = ctxs }, ct);
    }

    public async Task<bool> SendRecommendationAsync(
        ContractRecommendation rec,
        CancellationToken ct = default)
    {
        if (!rec.Window.IsActionable)
        {
            return false;
        }

        var title = $"0DTE · {rec.Symbol} · {rec.Strategy}";
        var body =
            $"{rec.Strategy} on {rec.Symbol}\n" +
            $"{rec.Reason}\n" +
            $"{rec.Window.Label} ({rec.Window.Score:F0}/100)\n" +
            $"{rec.Window.Details}\n" +
            (rec.SuggestedContract is { } c ? $"Contract: {c}\n" : "") +
            $"Price ${rec.Price.ToString("F2", CultureInfo.InvariantCulture)}";

        var tags = rec.Window.Kind == BuyWindowKind.PostNewsFade
            ? new[] { "warning" }
            : new[] { "rocket" };

        return await _notifier.SendAsync(title, body, tags, ct);
    }

    private static void AppendSymbol(StringBuilder sb, SymbolContext ctx)
    {
        sb.AppendLine();
        sb.AppendLine($"-- {ctx.Symbol} --");
        if (ctx.PreviousDayChangePercent is { } prev)
        {
            sb.AppendLine($"  Prev day: {Sign(prev)} (close {ctx.PreviousClose?.ToString("F2", CultureInfo.InvariantCulture)})");
        }
        if (ctx.PremarketChangePercent is { } pre)
        {
            sb.AppendLine($"  Premarket: {Sign(pre)} (-> {ctx.PremarketEnd?.ToString("F2", CultureInfo.InvariantCulture)})");
        }
        if (ctx.News.Count > 0)
        {
            sb.AppendLine("  News:");
            foreach (var n in ctx.News)
            {
                sb.AppendLine($"   * {n.Title}");
            }
        }
        else
        {
            sb.AppendLine("  News: none");
        }
    }

    private static string Sign(double value)
        => value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%";
}
