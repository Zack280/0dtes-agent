using System.Diagnostics;
using _0dtes_app.Models;
using _0dtes_app.Services;

namespace _0dtes_agent;

public sealed class AlertService
{
    private readonly AgentConfig _config;
    private readonly NtfyNotifier _notifier;
    private readonly YahooReferenceService _yahoo = new();
    private readonly BriefingChannel _briefing;
    private DateTime _lastBrief = DateTime.MinValue;

    public AlertService(AgentConfig config, NtfyNotifier notifier)
    {
        _config = config;
        _notifier = notifier;
        _briefing = new BriefingChannel(notifier, _yahoo);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var (chain, quotes) = BuildDataSources();
        await quotes.ConnectAsync(_config.Symbols, ct);

        var engines = new Dictionary<string, ScannerEngine>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in _config.Symbols)
        {
            engines[symbol] = new ScannerEngine();
        }

        var rules = _config.BuildScanRules();
        var interval = TimeSpan.FromSeconds(Math.Max(_config.ScanIntervalSeconds, 5));
        var briefInterval = TimeSpan.FromMinutes(Math.Max(_config.BriefIntervalMinutes, 0));
        var timer = Stopwatch.StartNew();

        Console.WriteLine($"0dtes agent scanning {string.Join(", ", _config.Symbols)} every {interval.TotalSeconds:0}s" +
                          $" in {_config.Mode} mode -> ntfy topic '{_config.NtfyTopic}'");

        while (!ct.IsCancellationRequested)
        {
            if (briefInterval > TimeSpan.Zero && _config.EnableBrief &&
                DateTime.Now - _lastBrief >= briefInterval)
            {
                await SendBriefAsync(ct);
            }

            try
            {
                foreach (var symbol in _config.Symbols)
                {
                    await ScanOnceAsync(chain, quotes, engines[symbol], rules, symbol, ct);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] scan failed: {ex.Message}");
            }

            var elapsed = timer.Elapsed;
            var remaining = interval - elapsed;
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(remaining, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            timer.Restart();
        }
    }

    /// <summary>
    /// One-shot scan for scheduled runs (e.g. cron): build sources, scan every
    /// symbol once, optionally send a brief, then exit. Returns the number of
    /// signals sent, or -1 if data sources couldn't be reached.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var (chain, quotes) = BuildDataSources();

        try
        {
            await quotes.ConnectAsync(_config.Symbols, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] connect failed: {ex.Message}");
            return -1;
        }

        // The quote pump fills _latest asynchronously after ConnectAsync; give it
        // a moment to produce at least one quote per symbol before scanning.
        foreach (var symbol in _config.Symbols)
        {
            var got = await WaitForQuoteAsync(quotes, symbol, TimeSpan.FromSeconds(20), ct);
            if (!got)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {symbol}: no quote within 20s.");
                return -1;
            }
        }

        var sent = 0;
        var scannedAny = false;
        var rules = _config.BuildScanRules();
        foreach (var symbol in _config.Symbols)
        {
            var engine = new ScannerEngine();
            try
            {
                var count = await ScanOnceAsync(chain, quotes, engine, rules, symbol, ct);
                if (count < 0)
                {
                    continue;
                }
                scannedAny = true;
                sent += count;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {symbol}: {ex.Message}");
            }
        }

        return scannedAny ? sent : -1;
    }

    private (IOptionChainService Chain, IQuoteStream Quotes) BuildDataSources()
    {
        var mode = _config.Mode?.Trim().ToLowerInvariant() ?? "mock";

        if (mode == "tradier")
        {
            var settings = new AgentSettings
            {
                Mode = BrokerMode.Tradier,
                IsPaper = _config.Paper,
            };
            var storage = new FileTokenStorage(Path.Combine(AppContext.BaseDirectory, "tradier_token.txt"));
            if (!string.IsNullOrWhiteSpace(_config.TradierToken))
            {
                storage.SaveTokenAsync(_config.TradierToken).GetAwaiter().GetResult();
            }
            var client = new TradierClient(settings, storage);
            var tradier = new TradierMarketDataService(client);
            return (tradier, tradier);
        }

        if (mode == "yahoo")
        {
            var yahoo = new YahooOptionChainService();
            return (yahoo, yahoo);
        }

        var mock = new MockMarketDataService();
        return (mock, mock);
    }

    private static async Task<bool> WaitForQuoteAsync(
        IQuoteStream quotes,
        string symbol,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await quotes.GetQuoteAsync(symbol, ct) is not null)
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<int> ScanOnceAsync(
        IOptionChainService chain,
        IQuoteStream quotes,
        ScannerEngine engine,
        IReadOnlyList<ScanRule> rules,
        string symbol,
        CancellationToken ct)
    {
        var spot = await quotes.GetQuoteAsync(symbol, ct);
        if (spot is null)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {symbol}: no quote yet, skipping.");
            return -1;
        }

        var expiries = await chain.GetExpiriesAsync(symbol, ct);
        var expiry = expiries.FirstOrDefault()?.Date ?? DateTime.Today;
        var rows = await chain.GetChainAsync(symbol, expiry, spot.Mid, ct);

        if (rows.Count == 0)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {symbol}: empty chain, skipping.");
            return -1;
        }

        var signals = engine.Evaluate(rules.ToList(), rows, spot, expiry, DateTime.Now, symbol);
        var ctx = await _yahoo.GetSymbolContextAsync(symbol, _config.NewsPerSymbol, ct);
        foreach (var signal in signals)
        {
            await NotifyAsync(symbol, signal, ctx, spot, ct);
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {symbol} @ {spot.Last:F2} expiry {expiry:yyyy-MM-dd} " +
                          $"rows {rows.Count} new signals {signals.Count}");
        return signals.Count;
    }

    private async Task SendBriefAsync(CancellationToken ct)
    {
        _lastBrief = DateTime.Now;
        try
        {
            var ok = await _briefing.SendBriefAsync(_config.Symbols, ct);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] brief: {(ok ? "sent" : "FAILED")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] brief failed: {ex.Message}");
        }
    }

    private async Task NotifyAsync(string symbol, ScanSignal signal, SymbolContext? ctx, QuoteTick spot, CancellationToken ct)
    {
        var title = $"0DTE · {symbol} · {signal.Strategy}";
        var body =
            $"{signal.Strategy} on {symbol}:\n" +
            $"{signal.Reason}\n" +
            $"Price ${signal.Price:F2} · {signal.Time:HH:mm:ss}";

        var tags = signal.Strategy switch
        {
            "IV spike" => new[] { "warning" },
            "Volume surge" => new[] { "chart_with_upwards_trend" },
            _ => new[] { "chart_with_upwards_trend" },
        };

        var ok = await _notifier.SendAsync(title, body, tags, ct);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] notify: {(ok ? "sent" : "FAILED")} '{title}'");

        if (!ok && !_notifier.IsConfigured)
        {
            Console.WriteLine("  (ntfy topic not configured — set NTFY_TOPIC or agent.json NtfyTopic)");
        }

        if (ctx is not null && RecommendationScorer.TryScore(signal, ctx, spot, out var rec))
        {
            var sent = await _briefing.SendRecommendationAsync(rec, ct);
            if (sent)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] rec sent: {rec.Window.Label} ({rec.Window.Score:F0})");
            }
        }
    }
}
