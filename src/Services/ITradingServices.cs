using _0dtes_app.Models;

namespace _0dtes_app.Services;

public enum BrokerMode
{
    Mock,
    Tradier
}

public interface ITradingSettings
{
    BrokerMode Mode { get; set; }
    bool IsPaper { get; set; }
    string ActiveSymbol { get; set; }
}

public interface ITokenStorage
{
    Task<string?> GetTokenAsync(CancellationToken ct = default);
    Task SaveTokenAsync(string token, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

public interface IOptionChainService
{
    Task<IReadOnlyList<OptionExpiry>> GetExpiriesAsync(string symbol, CancellationToken ct = default);

    Task<IReadOnlyList<OptionChainRow>> GetChainAsync(string symbol, DateTime expiry, double? spot = null, CancellationToken ct = default);
}

public interface IQuoteStream
{
    IObservable<QuoteTick> Stream { get; }

    Task<QuoteTick?> GetQuoteAsync(string symbol, CancellationToken ct = default);

    Task ConnectAsync(IEnumerable<string> symbols, CancellationToken ct = default);

    Task DisconnectAsync();
}

public interface IOrderService
{
    Task<OrderResult> PlaceOrderAsync(OptionOrder order, CancellationToken ct = default);

    Task<IReadOnlyList<OrderRecord>> GetOrdersAsync(CancellationToken ct = default);
}

public interface IPositionService
{
    Task<IReadOnlyList<OptionPosition>> GetPositionsAsync(CancellationToken ct = default);
}

public interface IAccountService
{
    Task<AccountSummary> GetAccountAsync(CancellationToken ct = default);
}
