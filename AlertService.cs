using System.Diagnostics;
using _0dtes_app.Models;
using _0dtes_app.Services;

namespace _0dtes_agent;

public sealed class AlertService
{
    private record PendingCapture(
        string AlertId,
        int HorizonMinutes,
        DateTime DueUtc,
        string Symbol,
        DateTime ExpiryUtc,
        double Strike,
        string Side);

    private static readonly TimeSpan CaptureHorizon15 = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CaptureHorizon60 = TimeSpan.FromMinutes(60);

    // Week-one labels: the 11:00 ET hour won 1/51 sent alerts (2%), worst of
    // any hour. Suppress sends there regardless of score.
    private static readonly int[] BlockedSendHoursEt = { 11 };

    private readonly AgentConfig _config;
    private readonly NtfyNotifier _notifier;
    private readonly YahooReferenceService _yahoo = new();
    private readonly BriefingChannel _briefing;
    private readonly AlertLogStore _log;
    private readonly OptionCaptureStore _captures;
    private readonly string? _regime;
    private readonly System.Net.Http.HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private DateTime _lastBrief = DateTime.MinValue;
    private readonly Dictionary<string, PendingCapture> _pendingCaptures = new(StringComparer.Ordinal);

    public AlertService(AgentConfig config, NtfyNotifier notifier)
    {
        _config = config;
        _notifier = notifier;
        _briefing = new BriefingChannel(notifier, _yahoo);
        _log = new AlertLogStore(config.DataDir);
        _captures = new OptionCaptureStore(config.DataDir);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
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

        SeedPendingCapturesFromRecentAlerts();

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
                await DispatchDueCapturesAsync(chain, ct);
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
        var alertId = Guid.NewGuid().ToString("N");
        var shouldSend = rec is not null &&
                         rec.Window.Score >= _config.MinAlertScore &&
                         !BlockedSendHoursEt.Contains(timeEt.Hour);

        // Log every candidate signal so we can later grade its outcome.
        _log.AppendAlert(new AlertRecord(
            alertId,
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
            Sent: shouldSend,
            Contract: contract,
            DaysToExpiry: dte,
            HourOfDay: timeEt.Hour,
            DayOfWeek: (int)nowUtc.DayOfWeek,
            Regime: _regime,
            DedupKey: dedupKey));

        ScheduleHorizonCaptures(alertId, symbol, contract, nowUtc);

        // Quality gate: only alert when the scored recommendation clears the bar.
        if (!shouldSend)
        {
            var reason = rec is null || rec.Window.Score < _config.MinAlertScore
                ? $"score {(rec?.Window.Score ?? 0):F0} < {_config.MinAlertScore}"
                : $"hour {timeEt.Hour:00} ET is suppressed (week-one data: 2% win rate)";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] skip '0DTE · {symbol} · {signal.Strategy}' ({reason})");
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

    /// <summary>
    /// Registers +15m and +60m live-quote captures for an alert so the tape can
    /// record the contract's real bid/ask at the labeling horizons. Capture is
    /// only meaningful while the contract is still trading, so we skip anything
    /// with a strike/side we can't re-resolve.
    /// </summary>
    private void ScheduleHorizonCaptures(string alertId, string symbol, ContractSnapshot? contract, DateTime signalUtc)
    {
        if (contract is null ||
            contract.Strike is not { } strike ||
            string.IsNullOrWhiteSpace(contract.Side))
        {
            return;
        }

        _pendingCaptures.TryAdd($"{alertId}|15", new PendingCapture(alertId, 15, signalUtc + CaptureHorizon15, symbol, contract.ExpiryUtc, strike, contract.Side!));
        _pendingCaptures.TryAdd($"{alertId}|60", new PendingCapture(alertId, 60, signalUtc + CaptureHorizon60, symbol, contract.ExpiryUtc, strike, contract.Side!));
    }

    /// <summary>
    /// Re-schedules horizon captures for alerts logged recently (e.g. during an
    /// earlier overlapping window) whose +15m/+60m quote may still be pending.
    /// This lets a +60m capture that crosses a window boundary complete in the
    /// next continuous run. Already-captured horizons are skipped.
    /// </summary>
    private void SeedPendingCapturesFromRecentAlerts()
    {
        var captured = _captures.LoadByAlertKey();
        var now = DateTime.UtcNow;
        var today = now.Date;

        foreach (var alert in _log.LoadAlerts())
        {
            if (alert.Contract is null || alert.SignalTimeUtc.Date != today)
            {
                continue;
            }
            if (now - alert.SignalTimeUtc > CaptureHorizon60)
            {
                continue;
            }

            var symbol = alert.Symbol;
            var contract = alert.Contract;
            if (contract.Strike is not { } strike || string.IsNullOrWhiteSpace(contract.Side))
            {
                continue;
            }

            if (!captured.ContainsKey($"{alert.Id}|15") && alert.SignalTimeUtc + CaptureHorizon15 > now)
            {
                _pendingCaptures[$"{alert.Id}|15"] = new PendingCapture(alert.Id, 15, alert.SignalTimeUtc + CaptureHorizon15, symbol, contract.ExpiryUtc, strike, contract.Side!);
            }
            if (!captured.ContainsKey($"{alert.Id}|60") && alert.SignalTimeUtc + CaptureHorizon60 > now)
            {
                _pendingCaptures[$"{alert.Id}|60"] = new PendingCapture(alert.Id, 60, alert.SignalTimeUtc + CaptureHorizon60, symbol, contract.ExpiryUtc, strike, contract.Side!);
            }
        }
    }

    /// <summary>
    /// Re-queries the live chain for any alert whose +15m/+60m horizon has
    /// arrived, records the contract's real quote into data/captures.jsonl, and
    /// drops the pending entry (whether or not a quote was found, so it doesn't
    /// retry forever). Catches transient chain failures gracefully — a missed
    /// capture simply falls back to greeks projection in the labeler.
    /// </summary>
    private async Task DispatchDueCapturesAsync(IOptionChainService chain, CancellationToken ct)
    {
        if (_pendingCaptures.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var due = _pendingCaptures
            .Where(kv => kv.Value.DueUtc <= now)
            .ToList();

        foreach (var (key, pending) in due)
        {
            _pendingCaptures.Remove(key);
            try
            {
                IReadOnlyList<OptionChainRow> rows;
                try
                {
                    rows = await chain.GetChainAsync(pending.Symbol, pending.ExpiryUtc, ct: ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] capture {key}: chain fetch failed ({ex.Message}); skipping.");
                    continue;
                }

                var row = rows.FirstOrDefault(r => Math.Abs(r.Strike - pending.Strike) < 0.005);
                var quote = pending.Side == "C" ? row?.Call : row?.Put;
                if (quote is null)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] capture {key}: contract not in chain; skipping.");
                    continue;
                }

                var spot = await GetLatestSpotAsync(pending.Symbol, ct);
                _captures.Append(new OptionCapture(
                    pending.AlertId,
                    pending.HorizonMinutes,
                    now,
                    pending.Symbol,
                    pending.ExpiryUtc,
                    pending.Strike,
                    pending.Side,
                    quote.Bid,
                    quote.Ask,
                    quote.Mid,
                    quote.Delta,
                    quote.Gamma,
                    quote.Theta,
                    quote.Vega,
                    quote.ImpliedVolatility,
                    spot));

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] capture {pending.AlertId[..6]}: " +
                                  $"{pending.Symbol} {pending.Strike}{pending.Side} " +
                                  $"@+{pending.HorizonMinutes}m bid {quote.Bid:F2} ask {quote.Ask:F2}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] capture {key} failed: {ex.Message}");
            }
        }
    }

    private async Task<double?> GetLatestSpotAsync(string symbol, CancellationToken ct)
    {
        try
        {
            var url = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "https://query1.finance.yahoo.com/v8/finance/chart/{0}?interval=1m&range=1d",
                Uri.EscapeDataString(symbol));
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var price = doc.RootElement
                .GetProperty("chart").GetProperty("result")[0]
                .GetProperty("meta").GetProperty("regularMarketPrice").GetDouble();
            return price;
        }
        catch
        {
            return null;
        }
    }
}
