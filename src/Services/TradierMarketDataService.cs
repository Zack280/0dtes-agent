using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace _0dtes_app.Services;

public class TradierMarketDataService : IOptionChainService, IQuoteStream
{
    private readonly TradierClient _client;
    private readonly object _gate = new();
    private readonly Dictionary<string, QuoteTick> _quotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SimpleSubject<QuoteTick> _ticks = new();
    private readonly HashSet<string> _symbols = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _pump;

    public TradierMarketDataService(TradierClient client)
    {
        _client = client;
    }

    public IObservable<QuoteTick> Stream => _ticks;

    public Task<QuoteTick?> GetQuoteAsync(string symbol, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_quotes.TryGetValue(symbol, out var quote) ? quote : null);
        }
    }

    public async Task<IReadOnlyList<OptionExpiry>> GetExpiriesAsync(string symbol, CancellationToken ct = default)
    {
        var json = await _client.GetRawAsync($"/markets/options/expirations?symbol={Uri.EscapeDataString(symbol)}&includeAllRoots=true&strikes=false", ct);
        var response = JsonSerializer.Deserialize(json, TradierJsonContext.Default.TradierExpirationsResponse);
        var dates = response?.expirations?.date ?? new List<string>();
        var list = new List<OptionExpiry>();
        foreach (var date in dates)
        {
            if (DateTime.TryParse(date, out var value))
            {
                list.Add(new OptionExpiry(value.Date));
            }
        }
        return list;
    }

    public async Task<IReadOnlyList<OptionChainRow>> GetChainAsync(string symbol, DateTime expiry, double? spot = null, CancellationToken ct = default)
    {
        var json = await _client.GetRawAsync($"/markets/options/chains?symbol={Uri.EscapeDataString(symbol)}&expiration={expiry:yyyy-MM-dd}&greeks=true", ct);
        var response = JsonSerializer.Deserialize(json, TradierJsonContext.Default.TradierChainResponse);
        var options = response?.options?.option ?? new List<TradierOptionContract>();
        var rows = options
            .Where(o => o.strike is not null)
            .GroupBy(o => Math.Round(o.strike!.Value, 2))
            .OrderBy(g => g.Key)
            .Select(group =>
            {
                var call = group.FirstOrDefault(o => string.Equals(o.type, "call", StringComparison.OrdinalIgnoreCase));
                var put = group.FirstOrDefault(o => string.Equals(o.type, "put", StringComparison.OrdinalIgnoreCase));
                return new OptionChainRow(group.Key, ToQuote(call), ToQuote(put));
            })
            .ToList();
        return rows;
    }

    private static OptionQuote? ToQuote(TradierOptionContract? contract)
    {
        if (contract is null)
        {
            return null;
        }
        var greeks = contract.greeks;
        return new OptionQuote(
            contract.bid ?? 0,
            contract.ask ?? 0,
            contract.last,
            greeks?.delta ?? 0,
            greeks?.gamma ?? 0,
            greeks?.theta ?? 0,
            greeks?.vega ?? 0,
            greeks?.mid_iv ?? 0,
            contract.volume ?? 0,
            contract.open_interest ?? 0);
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
            _pump = PumpAsync(_cts.Token);
        }
    }

    private string[] GetSymbols()
    {
        lock (_gate)
        {
            return _symbols.ToArray();
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var symbols = GetSymbols();
        if (symbols.Length == 0)
        {
            return;
        }
        if (await TryStartWebSocketAsync(symbols, ct).ConfigureAwait(false))
        {
            return;
        }
        await PollLoopAsync(symbols, ct).ConfigureAwait(false);
    }

    private async Task PollLoopAsync(string[] symbols, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(symbols, ct).ConfigureAwait(false);
            }
            catch
            {
            }
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
    }

    private async Task PollOnceAsync(string[] symbols, CancellationToken ct)
    {
        var query = string.Join(",", symbols);
        var json = await _client.GetRawAsync($"/markets/quotes?symbols={Uri.EscapeDataString(query)}", ct);
        var response = JsonSerializer.Deserialize(json, TradierJsonContext.Default.TradierQuotesResponse);
        var quotes = response?.quotes?.quote ?? new List<TradierQuote>();
        foreach (var quote in quotes)
        {
            var tick = ToTick(quote);
            if (tick is not null)
            {
                Publish(tick);
            }
        }
    }

    private async Task<bool> TryStartWebSocketAsync(string[] symbols, CancellationToken ct)
    {
        ClientWebSocket? socket = null;
        try
        {
            var token = await _client.GetTokenAsync(ct);
            var session = await _client.CreateStreamSessionAsync(string.Join(",", symbols), ct);
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(session))
            {
                return false;
            }
            var uri = new Uri($"{_client.StreamUrl}?access_token={Uri.EscapeDataString(token)}&sessionid={Uri.EscapeDataString(session)}");
            socket = new ClientWebSocket();
            await socket.ConnectAsync(uri, ct).ConfigureAwait(false);

            var buffer = new byte[4096];
            var pending = new StringBuilder();
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var segment = new ArraySegment<byte>(buffer);
                var result = await socket.ReceiveAsync(segment, ct).ConfigureAwait(false);
                pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                var chunk = pending.ToString();
                var lastNewline = chunk.LastIndexOf('\n');
                if (lastNewline < 0)
                {
                    continue;
                }
                pending.Clear();
                pending.Append(chunk.Substring(lastNewline + 1));
                foreach (var line in chunk.Substring(0, lastNewline).Split('\n'))
                {
                    var data = line.Trim();
                    if (!data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var payload = data.Substring(5).Trim();
                    if (payload.Length == 0)
                    {
                        continue;
                    }
                    var tick = ParseStreamTick(payload);
                    if (tick is not null)
                    {
                        Publish(tick);
                    }
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (socket is not null)
            {
                try
                {
                    socket.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private static QuoteTick? ParseStreamTick(string payload)
    {
        try
        {
            var quote = JsonSerializer.Deserialize(payload, TradierJsonContext.Default.TradierQuote);
            return ToTick(quote);
        }
        catch
        {
            return null;
        }
    }

    private static QuoteTick? ToTick(TradierQuote? quote)
    {
        if (quote is null || quote.last is null)
        {
            return null;
        }
        var last = quote.last.Value;
        var bid = quote.bid ?? last;
        var ask = quote.ask ?? last;
        return new QuoteTick(
            quote.symbol ?? "?",
            bid,
            ask,
            last,
            quote.change ?? 0,
            quote.change_percentage ?? 0,
            DateTime.Now);
    }

    private void Publish(QuoteTick tick)
    {
        lock (_gate)
        {
            _quotes[tick.Symbol] = tick;
        }
        _ticks.OnNext(tick);
    }
}
