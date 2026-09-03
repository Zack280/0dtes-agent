namespace _0dtes_app.Models;

public enum OptionRight
{
    Call,
    Put
}

public record OptionExpiry(DateTime Date)
{
    public string Label => Date == DateTime.Today ? "0DTE (Today)" : Date.ToString("MMM dd");
}

public record OptionQuote(
    double Bid,
    double Ask,
    double? Last,
    double Delta,
    double Gamma,
    double Theta,
    double Vega,
    double ImpliedVolatility,
    long Volume,
    long OpenInterest)
{
    public double Mid => (Bid + Ask) / 2;
}

public record OptionContract(
    string Symbol,
    DateTime Expiry,
    double Strike,
    OptionRight Right);

public record OptionChainRow(
    double Strike,
    OptionQuote? Call,
    OptionQuote? Put)
{
    public bool InTheMoney => Call is not null && Call.Delta > 0.5;
}
