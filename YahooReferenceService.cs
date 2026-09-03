using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace _0dtes_agent;

/// <summary>
/// Pulls market context (previous-day performance, premarket performance, recent news)
/// from Yahoo Finance's free public endpoints. No API token required.
/// </summary>
public sealed class YahooReferenceService : IDisposable
{
    private const string ChartUrlTemplate =
        "https://query1.finance.yahoo.com/v8/finance/chart/{0}?interval=1m&range=1d&includePrePost=true";

    private const string SearchUrlTemplate =
        "https://query1.finance.yahoo.com/v1/finance/search?q={0}&quotesCount=0&newsCount={1}&listsCount=0";

    private readonly HttpClient _http;

    public YahooReferenceService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
    }

    public async Task<SymbolContext?> GetSymbolContextAsync(
        string symbol,
        int newsCount = 3,
        CancellationToken ct = default)
    {
        var chartTask = GetChartContextAsync(symbol, ct);
        var newsTask = GetNewsAsync(symbol, newsCount, ct);
        var chart = await chartTask.ConfigureAwait(false);
        var news = await newsTask.ConfigureAwait(false);

        return new SymbolContext(
            symbol.ToUpperInvariant(),
            chart?.PreviousClose,
            chart?.RegularMarketChangePercent,
            chart?.PremarketStart,
            chart?.PremarketEnd,
            chart?.PremarketChangePercent,
            news);
    }

    public async Task<ChartContext?> GetChartContextAsync(string symbol, CancellationToken ct = default)
    {
        try
        {
            var url = string.Format(CultureInfo.InvariantCulture, ChartUrlTemplate, Uri.EscapeDataString(symbol));
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var response = System.Text.Json.JsonSerializer.Deserialize(
                json, YahooRefJsonContext.Default.YahooChartResponse);
            var meta = response?.chart?.result?.FirstOrDefault()?.meta;
            if (meta is null)
            {
                return null;
            }

            var previousClose = meta.previousClose ?? meta.chartPreviousClose;
            var regularMarketChangePercent = meta.regularMarketChangePercent;

            var premarket = ComputePremarket(response, meta.currentTradingPeriod?.pre?.start);
            double? premarketChangePercent = null;
            if (premarket is { } pm && previousClose is { } pc && pc > 0)
            {
                premarketChangePercent = (pm.Item2 - pc) / pc * 100;
            }

            return new ChartContext(
                regularMarketChangePercent,
                previousClose,
                premarket?.Item1,
                premarket?.Item2,
                premarketChangePercent);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<NewsItem>> GetNewsAsync(string symbol, int count = 3, CancellationToken ct = default)
    {
        try
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                SearchUrlTemplate,
                Uri.EscapeDataString(symbol),
                count);
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var response = System.Text.Json.JsonSerializer.Deserialize(
                json, YahooRefJsonContext.Default.YahooSearchResponse);

            return (response?.news ?? new())
                .Select(n => new NewsItem(n.title ?? "", n.publisher ?? "", n.link, UnixTime(n.providerPublishTime)))
                .Where(n => !string.IsNullOrWhiteSpace(n.Title))
                .Take(count)
                .ToList();
        }
        catch
        {
            return Array.Empty<NewsItem>();
        }
    }

    private static (double, double)? ComputePremarket(YahooChartResponse? response, long? preStart)
    {
        var result = response?.chart?.result?.FirstOrDefault();
        var ts = result?.timestamp;
        var close = result?.indicators?.quote?.FirstOrDefault()?.close;
        if (ts is null || close is null || preStart is null || ts.Count == 0)
        {
            return null;
        }

        double? start = null;
        double? end = null;
        for (var i = 0; i < ts.Count; i++)
        {
            if (ts[i] < preStart.Value)
            {
                continue;
            }
            if (close[i] is { } c && double.IsFinite(c))
            {
                start ??= c;
                end = c;
            }
            // Premarket stops at 09:30 regular open; stop collecting once we pass it.
            if (ts[i] >= preStart.Value + 19800) // +5.5h from 04:00 covers through 09:30
            {
                break;
            }
        }

        return start is { } s && end is { } e ? (s, e) : null;
    }

    private static DateTime UnixTime(long? seconds)
        => seconds is { } s
            ? DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime
            : DateTime.UnixEpoch;

    public void Dispose() => _http.Dispose();
}

public sealed record ChartContext(
    double? RegularMarketChangePercent,
    double? PreviousClose,
    double? PremarketStart,
    double? PremarketEnd,
    double? PremarketChangePercent);

public sealed class YahooChartResponse
{
    public YahooChartData? chart { get; set; }
}

public sealed class YahooChartData
{
    public List<YahooChartResult>? result { get; set; }
}

public sealed class YahooChartResult
{
    public YahooChartMeta? meta { get; set; }
    public List<long>? timestamp { get; set; }
    public YahooIndicators? indicators { get; set; }
}

public sealed class YahooChartMeta
{
    public double? regularMarketChangePercent { get; set; }
    public double? chartPreviousClose { get; set; }
    public double? previousClose { get; set; }
    public YahooCurrentTradingPeriod? currentTradingPeriod { get; set; }
}

public sealed class YahooCurrentTradingPeriod
{
    public YahooPeriod? pre { get; set; }
}

public sealed class YahooPeriod
{
    public long start { get; set; }
}

public sealed class YahooIndicators
{
    public List<YahooQuote>? quote { get; set; }
}

public sealed class YahooQuote
{
    public List<double?>? close { get; set; }
}

public class YahooSearchResponse
{
    public List<YahooNewsItem>? news { get; set; }
}

public class YahooNewsItem
{
    public string? title { get; set; }
    public string? publisher { get; set; }
    public string? link { get; set; }
    public long? providerPublishTime { get; set; }
}

[JsonSerializable(typeof(YahooChartResponse))]
[JsonSerializable(typeof(YahooSearchResponse))]
internal partial class YahooRefJsonContext : JsonSerializerContext
{
}
