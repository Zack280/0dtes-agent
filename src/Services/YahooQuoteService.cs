using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace _0dtes_app.Services;

public class YahooQuoteService : IQuoteStream
{
    private const string QuoteUrlTemplate = "https://query1.finance.yahoo.com/v8/finance/chart/{0}?interval=1m&range=1d";
    private const double PollIntervalSeconds = 30;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly SimpleSubject<QuoteTick> _ticks = new();
    private readonly Dictionary<string, QuoteTick> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _symbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _pump;

    public YahooQuoteService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
    }

    public IObservable<QuoteTick> Stream => _ticks;

    public Task<QuoteTick?> GetQuoteAsync(string symbol, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_latest.TryGetValue(symbol, out var quote) ? quote : null);
        }
    }

    public Task ConnectAsync(IEnumerable<string> symbols, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var symbol in symbols)
            {
                _symbols.Add(symbol);
            }
        }
        EnsurePump();
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
            _pump = null;
        }
        return Task.CompletedTask;
    }

    private void EnsurePump()
    {
        lock (_gate)
        {
            if (_pump is not null)
            {
                return;
            }
            _cts = new CancellationTokenSource();
            _pump = PollLoopAsync(_cts.Token);
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), ct).ConfigureAwait(false);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        string[] symbols;
        lock (_gate)
        {
            symbols = _symbols.ToArray();
        }
        foreach (var symbol in symbols)
        {
            try
            {
                var url = string.Format(QuoteUrlTemplate, Uri.EscapeDataString(symbol));
                var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                var response = JsonSerializer.Deserialize(json, YahooJsonContext.Default.YahooChartResponse);
                var meta = response?.chart?.result?.FirstOrDefault()?.meta;
                if (meta?.regularMarketPrice is not { } last)
                {
                    return;
                }
                var previousClose = meta.chartPreviousClose ?? meta.previousClose ?? last;
                var change = last - previousClose;
                var changePercent = previousClose <= 0 ? 0 : change / previousClose * 100;
                var timestamp = meta.regularMarketTime is { } time
                    ? DateTimeOffset.FromUnixTimeSeconds((long)time).LocalDateTime
                    : DateTime.Now;

                var tick = new QuoteTick(
                    symbol,
                    Math.Round(last - 0.01, 2),
                    Math.Round(last + 0.01, 2),
                    Math.Round(last, 2),
                    Math.Round(change, 2),
                    Math.Round(changePercent, 3),
                    timestamp);

                lock (_gate)
                {
                    _latest[tick.Symbol] = tick;
                }
                _ticks.OnNext(tick);
            }
            catch
            {
            }
        }
    }
}

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
}

public sealed class YahooChartMeta
{
    public double? regularMarketPrice { get; set; }

    public double? chartPreviousClose { get; set; }

    public double? previousClose { get; set; }

    public double? regularMarketTime { get; set; }
}

[JsonSerializable(typeof(YahooChartResponse))]
internal partial class YahooJsonContext : JsonSerializerContext
{
}
