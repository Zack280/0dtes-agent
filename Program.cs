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

var service = new AlertService(config, notifier);

// One-shot scan (used by scheduled/cron runs): scan every symbol once, exit.
if (args.Any(a => string.Equals(a, "--scan-once", StringComparison.OrdinalIgnoreCase)))
{
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
