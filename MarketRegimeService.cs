using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace _0dtes_agent;

/// <summary>
/// Offline market-regime context computed from free daily OHLC history (via the
/// Yahoo chart endpoint's interval=1d range=3mo). Produces one regime row per
/// completed trading day so alerts can be categorised (vol + trend) even for
/// days before the seed ran. Used to train regime-dependent behavior later.
/// </summary>
public sealed class MarketRegimeService : IDisposable
{
    /// <summary>Trailing-day window for realized-vol estimation.</summary>
    public const int VolWindowDays = 20;

    /// <summary>High-vol threshold in annualized %.</summary>
    public const double HighVolThreshold = 30.0;

    /// <summary>Trend drift threshold over the vol window, in basis points/day.</summary>
    public const double TrendBpPerDay = 15.0;

    private const string HistoryUrlTemplate =
        "https://query1.finance.yahoo.com/v8/finance/chart/{0}?interval=1d&range=3mo";

    private readonly HttpClient _http;

    public MarketRegimeService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
    }

    /// <summary>
    /// Builds one regime row per trading day from the symbol's daily closes.
    /// The row for date D uses the 20-day window ending at D's completed close.
    /// </summary>
    public async Task<List<RegimeDay>> BuildDailyHistoryAsync(string symbol, CancellationToken ct = default)
    {
        var series = await FetchDailyClosesAsync(symbol, ct);
        var rows = new List<RegimeDay>();
        for (var i = VolWindowDays; i < series.Count; i++)
        {
            var window = series.Skip(i - VolWindowDays + 1).Take(VolWindowDays).Select(c => c.Close).ToList();
            var withPrevious = series.Skip(i - VolWindowDays).Take(VolWindowDays + 1).ToList();
            var date = DateOnly.FromDateTime(series[i].Date.Date);
            rows.Add(new RegimeDay(
                date,
                series[i].Close,
                RealizedVolAnnualized(window),
                DriftBpPerDay(withPrevious),
                Classify(withPrevious)));
        }
        return rows;
    }

    /// <summary>Classifies closes into a vol-trend bucket using the latest window.</summary>
    public static string? Classify(IReadOnlyList<(DateTime Date, double Close)> closes)
    {
        if (closes.Count < VolWindowDays + 1)
        {
            return null;
        }
        var window = closes.Skip(closes.Count - VolWindowDays).Select(c => c.Close).ToList();
        var annualized = RealizedVolAnnualized(window);
        var drift = DriftBpPerDay(closes);

        var vol = annualized >= HighVolThreshold ? "highVol" : "lowVol";
        var trend = drift is null
            ? "flat"
            : drift.Value > TrendBpPerDay ? "bull"
            : drift.Value < -TrendBpPerDay ? "bear"
            : "flat";

        return $"{vol}-{trend}";
    }

    public static double RealizedVolAnnualized(IReadOnlyList<double> closes)
    {
        if (closes.Count < 2)
        {
            return 0;
        }
        var logRets = new List<double>(closes.Count);
        for (var i = 1; i < closes.Count; i++)
        {
            if (closes[i - 1] > 0)
            {
                logRets.Add(Math.Log(closes[i] / closes[i - 1]));
            }
        }
        if (logRets.Count == 0)
        {
            return 0;
        }
        var mean = logRets.Average();
        var variance = logRets.Sum(r => (r - mean) * (r - mean)) / (logRets.Count - 1);
        return Math.Sqrt(variance) * Math.Sqrt(252) * 100;
    }

    /// <summary>Approximate per-day linear drift in basis points across the window.</summary>
    public static double? DriftBpPerDay(IReadOnlyList<(DateTime Date, double Close)> closes)
    {
        if (closes.Count < VolWindowDays + 1)
        {
            return null;
        }
        var first = closes[closes.Count - 1 - VolWindowDays].Close;
        var last = closes[^1].Close;
        if (first <= 0)
        {
            return null;
        }
        return (last / first - 1) / VolWindowDays * 1e4; // bp/day
    }

    private async Task<List<(DateTime Date, double Close)>> FetchDailyClosesAsync(string symbol, CancellationToken ct)
    {
        try
        {
            var url = string.Format(CultureInfo.InvariantCulture, HistoryUrlTemplate, Uri.EscapeDataString(symbol));
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize(json, RegimeJsonContext.Default.RegimeResponse);
            var result = response?.chart?.result?.FirstOrDefault();
            var ts = result?.timestamp;
            var closes = result?.indicators?.quote?.FirstOrDefault()?.close;
            if (ts is null || closes is null || ts.Count == 0)
            {
                return new List<(DateTime, double)>();
            }
            var series = new List<(DateTime, double)>();
            for (var i = 0; i < ts.Count && i < closes.Count; i++)
            {
                if (closes[i] is { } c && double.IsFinite(c) && c > 0)
                {
                    series.Add((DateTimeOffset.FromUnixTimeSeconds(ts[i]).UtcDateTime, c));
                }
            }
            return series;
        }
        catch
        {
            return new List<(DateTime, double)>();
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>One seeded regime day (see MarketRegimeService.BuildDailyHistoryAsync).</summary>
public sealed record RegimeDay(
    DateOnly Date,
    double Close,
    double RealizedVolAnn,
    double? DriftBp,
    string? Regime);

/// <summary>Snapshot of the last seeded regime, one line from regime.jsonl.</summary>
public sealed record RegimeSnapshot(
    DateOnly Date,
    string Regime);

public class RegimeResponse
{
    public RegimeChartData? chart { get; set; }
}

public class RegimeChartData
{
    public List<RegimeChartResult>? result { get; set; }
}

public class RegimeChartResult
{
    public List<long>? timestamp { get; set; }
    public RegimeIndicator? indicators { get; set; }
}

public class RegimeIndicator
{
    public List<RegimeQuote>? quote { get; set; }
}

public class RegimeQuote
{
    public List<double?>? close { get; set; }
}

[JsonSerializable(typeof(RegimeResponse))]
internal partial class RegimeJsonContext : JsonSerializerContext
{
}