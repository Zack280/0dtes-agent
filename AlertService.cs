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

    private async Task ScanOnceAsync(
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
            return;
        }

        var expiries = await chain.GetExpiriesAsync(symbol, ct);
        var expiry = expiries.FirstOrDefault()?.Date ?? DateTime.Today;
        var rows = await chain.GetChainAsync(symbol, expiry, spot.Mid, ct);

        var signals = engine.Evaluate(rules.ToList(), rows, spot, expiry, DateTime.Now, symbol);
        var ctx = await _yahoo.GetSymbolContextAsync(symbol, _config.NewsPerSymbol, ct);
        foreach (var signal in signals)
        {
            await NotifyAsync(symbol, signal, ctx, spot, ct);
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {symbol} @ {spot.Last:F2} expiry {expiry:yyyy-MM-dd} " +
                          $"rows {rows.Count} new signals {signals.Count}");
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
