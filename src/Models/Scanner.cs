namespace _0dtes_app.Models;

public enum ScanRuleKind
{
    DeltaSweetSpot,
    VolumeSurge,
    IvSpike,
    PriceLevelCross,
    ExpiryWindow
}

public record ScanRule(ScanRuleKind Kind, string Name, string Description, bool IsEnabled = true);

public record ScanSignal(
    string Symbol,
    string Strategy,
    string Reason,
    double Price,
    DateTime Time,
    double? Strike = null,
    string? Side = null,
    OptionQuote? Quote = null);
