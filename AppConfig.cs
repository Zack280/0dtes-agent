using System.Text.Json;
using System.Text.Json.Serialization;
using _0dtes_app.Models;

namespace _0dtes_agent;

public sealed record RuleConfig(ScanRuleKind Kind, bool IsEnabled = true);

public sealed class AgentConfig
{
    public string NtfyTopic { get; set; } = "";

    public List<string> Symbols { get; set; } = ["SOFI", "SPY"];

    public int ScanIntervalSeconds { get; set; } = 30;

    /// <summary>mock, yahoo, or tradier</summary>
    public string Mode { get; set; } = "mock";

    public string TradierToken { get; set; } = "";

    public bool Paper { get; set; } = true;

    /// <summary>Send a market-context brief (prev-day, premarket, news).</summary>
    public bool EnableBrief { get; set; } = true;

    /// <summary>Minutes between scheduled briefs (0 = only on demand via --brief).</summary>
    public int BriefIntervalMinutes { get; set; } = 0;

    public int NewsPerSymbol { get; set; } = 3;

    /// <summary>Directory for the alert/label dataset (for future training).</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>Only alerts scoring at/above this are sent. Lower = more alerts.</summary>
    public double MinAlertScore { get; set; } = 50;

    public List<RuleConfig> Rules { get; set; } =
    [
        new(ScanRuleKind.DeltaSweetSpot),
        new(ScanRuleKind.VolumeSurge),
        new(ScanRuleKind.IvSpike),
        new(ScanRuleKind.PriceLevelCross),
        new(ScanRuleKind.ExpiryWindow),
    ];

    public static AgentConfig Load(string[] args)
    {
        var path = "agent.json";
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase))
            {
                path = args[i + 1];
            }
        }

        var config = new AgentConfig();
        if (File.Exists(path))
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter() },
                };
                config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), options) ?? new AgentConfig();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to read config '{path}': {ex.Message}");
                Console.Error.WriteLine("Continuing with defaults.");
            }
        }

        var envTopic = Environment.GetEnvironmentVariable("NTFY_TOPIC");
        if (!string.IsNullOrWhiteSpace(envTopic))
        {
            config.NtfyTopic = envTopic;
        }

        var envToken = Environment.GetEnvironmentVariable("TRADIER_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            config.TradierToken = envToken;
        }

        return config;
    }

    public List<ScanRule> BuildScanRules()
    {
        var map = Rules
            .GroupBy(r => r.Kind)
            .ToDictionary(g => g.Key, g => g.Last().IsEnabled);

        return
        [
            new(ScanRuleKind.DeltaSweetSpot, "Delta sweet spot", "|Delta| 0.20-0.50", Enabled(map, ScanRuleKind.DeltaSweetSpot)),
            new(ScanRuleKind.VolumeSurge, "Volume surge", "Volume above 2x median", Enabled(map, ScanRuleKind.VolumeSurge)),
            new(ScanRuleKind.IvSpike, "IV spike", "IV above 1.5x ATM", Enabled(map, ScanRuleKind.IvSpike)),
            new(ScanRuleKind.PriceLevelCross, "Price level cross", "Round level cross", Enabled(map, ScanRuleKind.PriceLevelCross)),
            new(ScanRuleKind.ExpiryWindow, "Expiry window", "Within 2h of close", Enabled(map, ScanRuleKind.ExpiryWindow)),
        ];
    }

    private static bool Enabled(Dictionary<ScanRuleKind, bool> map, ScanRuleKind kind)
        => map.TryGetValue(kind, out var enabled) && enabled;
}
