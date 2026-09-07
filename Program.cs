using _0dtes_agent;

Console.WriteLine($"0dtes agent · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

var config = AgentConfig.Load(args);
var notifier = new NtfyNotifier(config.NtfyTopic);
if (!notifier.IsConfigured)
{
    Console.WriteLine("WARNING: no ntfy topic configured. Set NTFY_TOPIC env var or agent.json 'ntfyTopic'.");
    Console.WriteLine("Grab a free topic at https://ntfy.sh and subscribe in the ntfy app to receive alerts.");
}

// On-demand one-shot brief: run, send, exit.
if (args.Any(a => string.Equals(a, "--brief", StringComparison.OrdinalIgnoreCase)))
{
    using var yahoo = new YahooReferenceService();
    var briefing = new BriefingChannel(notifier, yahoo);
    var ok = await briefing.SendBriefAsync(config.Symbols, CancellationToken.None);
    Console.WriteLine(ok ? "Brief sent." : "Brief FAILED to send.");
    return ok ? 0 : 1;
}

// Grade past alerts against the underlying's subsequent moves.
if (args.Any(a => string.Equals(a, "--label", StringComparison.OrdinalIgnoreCase)))
{
    using var yahoo = new YahooReferenceService();
    var store = new AlertLogStore(config.DataDir);
    var labeler = new OutcomeLabeler(store, yahoo);
    var n = await labeler.LabelAsync(CancellationToken.None);
    Console.WriteLine($"Labeled {n} alert(s).");
    return 0;
}

// Seed/persist the market-regime history (vol + trend per trading day).
if (args.Any(a => string.Equals(a, "--regime", StringComparison.OrdinalIgnoreCase)))
{
    var symbol = "SPY";
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--regime-symbol", StringComparison.OrdinalIgnoreCase))
        {
            symbol = args[i + 1];
        }
    }
    using var regime = new MarketRegimeService();
    var store = new RegimeStore(config.DataDir);
    var days = await regime.BuildDailyHistoryAsync(symbol, CancellationToken.None);
    store.WriteAll(days);
    var latest = days.LastOrDefault();
    Console.WriteLine(latest is null
        ? "Regime seed: no complete days returned."
        : $"Regime seed: {days.Count} day(s) written to {store.RegimePath}; last {latest.Date} -> {latest.Regime}");
    return latest is null ? 1 : 0;
}

var service = new AlertService(config, notifier);

// One-shot scan (used by scheduled/cron runs): scan every symbol once, exit.
if (args.Any(a => string.Equals(a, "--scan-once", StringComparison.OrdinalIgnoreCase)))
{
    if (!MarketHours.IsOpen(DateTime.Now))
    {
        Console.WriteLine("US market closed; skipping scan.");
        return 0;
    }
    var sent = await service.RunOnceAsync(CancellationToken.None);
    Console.WriteLine($"Scan complete. Signals sent: {Math.Max(sent, 0)}");
    return sent >= 0 ? 0 : 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await service.RunAsync(cts.Token);
Console.WriteLine("Agent stopped.");
return 0;
