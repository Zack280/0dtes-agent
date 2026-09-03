namespace _0dtes_app.Services;

public class MockMarketDataService : IOptionChainService, IQuoteStream
{
    private readonly object _gate = new();
    private readonly Dictionary<string, QuoteTick> _quotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SimpleSubject<QuoteTick> _ticks = new();
    private readonly HashSet<string> _symbols = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _pump;

    public MockMarketDataService()
    {
        _quotes["SPY"] = new QuoteTick("SPY", 512.25, 512.45, 512.34, 2.14, 0.42, DateTime.Now);
    }

    public Task<IReadOnlyList<OptionExpiry>> GetExpiriesAsync(string symbol, CancellationToken ct = default)
        => Task.FromResult(MockChainData.Expiries(DateTime.Today));

    public Task<IReadOnlyList<OptionChainRow>> GetChainAsync(string symbol, DateTime expiry, double? spot = null, CancellationToken ct = default)
    {
        var current = spot ?? GetSpot(symbol);
        return Task.FromResult(MockChainData.BuildChain(current, expiry));
    }

    public IObservable<QuoteTick> Stream => _ticks;

    public Task<QuoteTick?> GetQuoteAsync(string symbol, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_quotes.TryGetValue(symbol, out var quote) ? quote : null);
        }
    }

    public Task ConnectAsync(IEnumerable<string> symbols, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var symbol in symbols)
            {
                _symbols.Add(symbol);
                if (!_quotes.ContainsKey(symbol))
                {
                    _quotes[symbol] = new QuoteTick(symbol, 100.10, 100.30, 100.20, 0, 0, DateTime.Now);
                }
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

    private double GetSpot(string symbol)
    {
        lock (_gate)
        {
            return _quotes.TryGetValue(symbol, out var quote) ? quote.Mid : MockChainData.DefaultSpot(symbol);
        }
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
            _pump = PumpAsync(_cts.Token);
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var random = new Random(42);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            QuoteTick[] ticks;
            lock (_gate)
            {
                ticks = _symbols.Select(symbol =>
                {
                    var current = _quotes[symbol];
                    var move = (random.NextDouble() - 0.5) * 0.08;
                    var last = Math.Max(0.5, current.Last + move);
                    var change = current.Change + (last - current.Last);
                    var changePercent = change == 0 ? 0 : change / (last - change) * 100;
                    var spread = Math.Max(0.02, last * 0.0004);
                    return new QuoteTick(
                        symbol,
                        Math.Round(last - spread, 2),
                        Math.Round(last + spread, 2),
                        Math.Round(last, 2),
                        Math.Round(change, 2),
                        Math.Round(changePercent, 3),
                        DateTime.Now);
                }).ToArray();
            }
            foreach (var tick in ticks)
            {
                _ticks.OnNext(tick);
            }
        }
    }
}
