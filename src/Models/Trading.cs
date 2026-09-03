namespace _0dtes_app.Models;

public enum OrderSide
{
    Buy,
    Sell
}

public enum OrderType
{
    Market,
    Limit
}

public enum OrderAction
{
    Open,
    Close
}

public record OptionOrder(
    OptionContract Contract,
    OrderSide Side,
    OrderAction Action,
    OrderType Type,
    int Quantity,
    double? LimitPrice,
    string? Status = null)
{
    public string Summary => $"{Side} {Quantity} {Contract.Symbol} {Contract.Strike} {Contract.Right} {Contract.Expiry:MMM dd}";
}

public record OptionPosition(
    OptionContract Contract,
    int Quantity,
    double AveragePrice,
    double LastPrice)
{
    public double Pnl => (LastPrice - AveragePrice) * Quantity * 100;
    public double PnlPercent => AveragePrice == 0 ? 0 : Pnl / (AveragePrice * Math.Abs(Quantity) * 100);
}

public record AccountSummary(
    double Cash,
    double Equity,
    double BuyingPower,
    double DayPnl,
    double DayPnlPercent);

public record OrderResult(
    bool Success,
    string OrderId,
    string Status,
    string? Error = null);

public record OrderRecord(
    string OrderId,
    OptionContract Contract,
    OrderSide Side,
    int Quantity,
    OrderType Type,
    double? LimitPrice,
    string Status,
    DateTime CreatedAt,
    double? AverageFillPrice = null);
