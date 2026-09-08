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
    private readonly AlertLogStore _log;
    private readonly string? _regime;
    private DateTime _lastBrief = DateTime.MinValue;

    public AlertService(AgentConfig config, NtfyNotifier notifier)
    {
        _config = config;
        _notifier = notifier;
        _briefing = new BriefingChannel(notifier, _yahoo);
        _log = new AlertLogStore(config.DataDir);
        _regime = new RegimeStore(config.DataDir).LoadLatest()?.Regime;
    }

    public async Task RunAsync(CancellationToken ct, TimeSpan? window = null)
    {
        var (chain, quotes) = BuildDataSources();
        await quotes.ConnectAsync(_config.Symbols, ct);

        var engines = new Dictionary<string, ScannerEngine>(StringComparer.OrdinalIgnoreCase);
        var todayKeys = LoadTodayDedupKeys();
        foreach (var symbol in _config.Symbols)
        {
            var engine = new ScannerEngine();
            engine.SeedEmitted(todayKeys.Where(k => StartsWithSymbol(k, symbol)));
            engines[symbol] = engine;
        }

        var rules = _config.BuildScanRules();
        var interval = TimeSpan.FromSeconds(Math.Max(_config.ScanIntervalSeconds, 5));
        var briefInterval = TimeSpan.FromMinutes(Math.Max(_config.BriefIntervalMinutes, 0));
        var timer = Stopwatch.StartNew();
        var windowStart = DateTime.UtcNow;

        Console.WriteLine($"0dtes agent scanning {string.Join(", ", _config.Symbols)} every {interval.TotalSeconds:0}s" +
                          $" in {_config.Mode} mode -> ntfy topic '{_config.NtfyTopic}'" +
                          (window is { } w ? $" for up to {w.TotalMinutes:0} min" : ""));

        while (!ct.IsCancellationRequested)
        {
            if (window is { } max && DateTime.UtcNow - windowStart >= max)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] run window elapsed; exiting cleanly.");
                break;
            }

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
            var contract = ResolveContract(signal, rows, expiry, spot.Mid);
            await NotifyAsync(symbol, signal, ctx, spot, contract, ct);
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

    private async Task NotifyAsync(string symbol, ScanSignal signal, SymbolContext? ctx, QuoteTick spot, ContractSnapshot? contract, CancellationToken ct)
    {
        var rec = ctx is not null && RecommendationScorer.TryScore(signal, ctx, spot, out var r) ? r : null;

        var nowUtc = DateTime.UtcNow;
        var timeEt = UsEastern.ToLocal(nowUtc);
        var dte = contract is null
            ? (double?)null
            : Math.Max(0, (contract.ExpiryUtc.Date - nowUtc.Date).TotalDays);

        var dedupKey = $"{symbol.ToUpperInvariant()}|{ScannerEngine.KeyFor(signal)}";

        // Log every candidate signal so we can later grade its outcome.
        _log.AppendAlert(new AlertRecord(
            Guid.NewGuid().ToString("N"),
            nowUtc,
            symbol,
            signal.Strategy,
            OutcomeLabeler.DirectionOf(signal.Strategy),
            spot.Last,
            ctx?.PreviousDayChangePercent ?? 0,
            ctx?.PremarketChangePercent ?? 0,
            ctx?.News.Count ?? 0,
            rec?.Window.Score ?? 0,
            rec?.Window.Kind.ToString() ?? "",
            Sent: rec is not null && rec.Window.Score >= _config.MinAlertScore,
            Contract: contract,
            DaysToExpiry: dte,
            HourOfDay: timeEt.Hour,
            DayOfWeek: (int)nowUtc.DayOfWeek,
            Regime: _regime,
            DedupKey: dedupKey));

        // Quality gate: only alert when the scored recommendation clears the bar.
        if (rec is null || rec.Window.Score < _config.MinAlertScore)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] skip '0DTE · {symbol} · {signal.Strategy}' " +
                              $"(score {(rec?.Window.Score ?? 0):F0} < {_config.MinAlertScore})");
            return;
        }

        var title = $"0DTE · {symbol} · {signal.Strategy}";
        var body =
            $"{signal.Strategy} on {symbol}:\n" +
            $"{signal.Reason}\n" +
            $"{rec.Window.Label} · Score {rec.Window.Score:F0}/100\n" +
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

        var sent = await _briefing.SendRecommendationAsync(rec, ct);
        if (sent)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] rec sent: {rec.Window.Label} ({rec.Window.Score:F0})");
        }
    }

    /// <summary>
    /// Picks the exact option the agent would trade for a signal. Uses the
    /// scanner's own quote when the rule produced one; otherwise resolves the
    /// nearest-ATM contract on the signal's side (bull&rarr;call, bear&rarr;put,
    /// neutral&rarr;call) so every alert is labelable with option P&amp;L.
    /// </summary>
    private static ContractSnapshot? ResolveContract(
        ScanSignal signal,
        IReadOnlyList<OptionChainRow> rows,
        DateTime expiry,
        double spotMid)
    {
        var (strike, side, quote) = signal.Quote is not null
            ? (signal.Strike, signal.Side, signal.Quote)
            : ResolveDefaultContract(rows, spotMid, OutcomeLabeler.DirectionOf(signal.Strategy));
        if (quote is null)
        {
            return null;
        }
        return new ContractSnapshot(
            strike,
            side,
            expiry.ToUniversalTime(),
            quote.Bid,
            quote.Ask,
            quote.Mid,
            quote.Delta,
            quote.Gamma,
            quote.Theta,
            quote.Vega,
            quote.ImpliedVolatility);
    }

    private static (double? Strike, string? Side, OptionQuote? Quote) ResolveDefaultContract(
        IReadOnlyList<OptionChainRow> rows,
        double spotMid,
        int direction)
    {
        if (rows.Count == 0)
        {
            return (null, null, null);
        }
        var atm = rows
            .OrderBy(r => Math.Abs(r.Strike - spotMid))
            .FirstOrDefault(r => (direction >= 0 && r.Call is not null) || (direction < 0 && r.Put is not null));
        atm ??= rows.OrderBy(r => Math.Abs(r.Strike - spotMid)).FirstOrDefault();
        if (atm is null)
        {
            return (null, null, null);
        }
        if (direction < 0 && atm.Put is { } put)
        {
            return (atm.Strike, "P", put);
        }
        if (atm.Call is { } call)
        {
            return (atm.Strike, "C", call);
        }
        if (atm.Put is { } p)
        {
            return (atm.Strike, "P", p);
        }
        return (null, null, null);
    }

    /// <summary>
    /// Load persistent de-dup keys for alerts logged today so continuous-window
    /// runs do not re-emit signals already captured in an earlier overlapping
    /// window. The stored key is symbol-scoped: "SYMBOL|RuleKind|strike|side".
    /// </summary>
    private IEnumerable<string> LoadTodayDedupKeys()
    {
        DateTime todayUtc = DateTime.UtcNow.Date;
        return _log.LoadAlerts()
            .Where(a => a.SignalTimeUtc.Date == todayUtc && !string.IsNullOrWhiteSpace(a.DedupKey))
            .Select(a => a.DedupKey!);
    }

    private static bool StartsWithSymbol(string key, string symbol)
        => key.StartsWith(symbol.ToUpperInvariant() + "|", StringComparison.Ordinal);
}
