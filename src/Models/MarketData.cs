namespace _0dtes_app.Models;

public record QuoteTick(
    string Symbol,
    double Bid,
    double Ask,
    double Last,
    double Change,
    double ChangePercent,
    DateTime Timestamp)
{
    public double Mid => (Bid + Ask) / 2;
}
