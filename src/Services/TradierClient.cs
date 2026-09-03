using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using _0dtes_app.Models;

namespace _0dtes_app.Services;

public sealed class TradierClient
{
    private readonly ITradingSettings _settings;
    private readonly ITokenStorage _storage;
    private readonly HttpClient _http;
    private readonly object _accountGate = new();
    private string? _accountId;

    public TradierClient(ITradingSettings settings, ITokenStorage storage)
    {
        _settings = settings;
        _storage = storage;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public string BaseUrl => _settings.IsPaper ? "https://sandbox.tradier.com/v1" : "https://api.tradier.com/v1";

    public string StreamUrl => _settings.IsPaper ? "wss://sandbox.stream.tradier.com/v1/markets/events" : "wss://stream.tradier.com/v1/markets/events";

    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
        => await _storage.GetTokenAsync(ct);

    public Task<string> GetRawAsync(string path, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return SendAsync(request, ct);
    }

    public Task<string> PostFormRawAsync(string path, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{path}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(form);
        return SendAsync(request, ct);
    }

    public Task<string> PostJsonRawAsync(string path, string jsonBody, string accept = "application/json", CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{path}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return SendAsync(request, ct);
    }

    public async Task<string?> GetAccountIdAsync(CancellationToken ct = default)
    {
        lock (_accountGate)
        {
            if (_accountId is not null)
            {
                return _accountId;
            }
        }
        var json = await GetRawAsync("/user/profile", ct);
        var response = JsonSerializer.Deserialize(json, TradierJsonContext.Default.TradierProfileResponse);
        var id = response?.profile?.account?.FirstOrDefault()?.account_number;
        lock (_accountGate)
        {
            _accountId = id;
        }
        return id;
    }

    public async Task<string?> CreateStreamSessionAsync(string symbols, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new TradierSessionRequest { events = "quote", symbols = symbols }, TradierJsonContext.Default.TradierSessionRequest);
        var json = await PostJsonRawAsync("/markets/events/session", body, accept: "text/event-stream", ct);
        var response = JsonSerializer.Deserialize(json, TradierJsonContext.Default.TradierSessionResponse);
        return response?.stream?.sessionid;
    }

    public static string ToOccSymbol(OptionContract contract)
    {
        var right = contract.Right == OptionRight.Call ? 'C' : 'P';
        var strike = (long)Math.Round(contract.Strike * 1000);
        return $"{contract.Symbol.ToUpperInvariant()}{contract.Expiry:yyMMdd}{right}{strike:00000000}";
    }

    public static bool TryParseOccSymbol(string? occ, out OptionContract contract)
    {
        contract = default!;
        if (string.IsNullOrWhiteSpace(occ))
        {
            return false;
        }
        if (occ.Length < 16)
        {
            return false;
        }
        var right = occ[^9];
        var expiryText = occ.Substring(occ.Length - 15, 6);
        var strikeText = occ.Substring(occ.Length - 8, 8);
        var symbol = occ.Substring(0, occ.Length - 15);
        if (!DateTime.TryParseExact(expiryText, "yyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var expiry))
        {
            return false;
        }
        if (!long.TryParse(strikeText, out var strikeRaw))
        {
            return false;
        }
        contract = new OptionContract(symbol, expiry.Date, strikeRaw / 1000.0, right == 'P' ? OptionRight.Put : OptionRight.Call);
        return true;
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await _storage.GetTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("No Tradier API token configured.");
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Tradier API {(int)response.StatusCode}: {body}");
        }
        return body;
    }
}

public sealed class FlexibleListConverter<T> : JsonConverter<List<T>>
{
    public override List<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            return (List<T>?)JsonSerializer.Deserialize(ref reader, options.GetTypeInfo(typeof(List<T>)));
        }
        var single = (T?)JsonSerializer.Deserialize(ref reader, options.GetTypeInfo(typeof(T)));
        return single is null ? new List<T>() : new List<T> { single };
    }

    public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, options.GetTypeInfo(typeof(List<T>)));
}

public sealed class TradierExpirationsResponse
{
    public TradierExpirationsData? expirations { get; set; }
}

public sealed class TradierExpirationsData
{
    [JsonConverter(typeof(FlexibleListConverter<string>))]
    public List<string>? date { get; set; }
}

public sealed class TradierChainResponse
{
    public TradierChainData? options { get; set; }
}

public sealed class TradierChainData
{
    [JsonConverter(typeof(FlexibleListConverter<TradierOptionContract>))]
    public List<TradierOptionContract>? option { get; set; }
}

public sealed class TradierOptionContract
{
    public string? symbol { get; set; }
    public string? type { get; set; }
    public double? strike { get; set; }
    public string? expiration_date { get; set; }
    public double? bid { get; set; }
    public double? ask { get; set; }
    public double? last { get; set; }
    public long? volume { get; set; }
    public long? open_interest { get; set; }
    public TradierGreeks? greeks { get; set; }
}

public sealed class TradierGreeks
{
    public double? delta { get; set; }
    public double? gamma { get; set; }
    public double? theta { get; set; }
    public double? vega { get; set; }
    public double? mid_iv { get; set; }
}

public sealed class TradierQuotesResponse
{
    public TradierQuotesData? quotes { get; set; }
}

public sealed class TradierQuotesData
{
    [JsonConverter(typeof(FlexibleListConverter<TradierQuote>))]
    public List<TradierQuote>? quote { get; set; }
}

public sealed class TradierQuote
{
    public string? symbol { get; set; }
    public double? last { get; set; }
    public double? change { get; set; }
    public double? change_percentage { get; set; }
    public double? bid { get; set; }
    public double? ask { get; set; }
    public double? previous_close { get; set; }
}

public sealed class TradierSessionRequest
{
    public string? events { get; set; }
    public string? symbols { get; set; }
}

public sealed class TradierSessionResponse
{
    public TradierSessionData? stream { get; set; }
}

public sealed class TradierSessionData
{
    public string? sessionid { get; set; }
}

public sealed class TradierProfileResponse
{
    public TradierProfile? profile { get; set; }
}

public sealed class TradierProfile
{
    public string? id { get; set; }

    [JsonConverter(typeof(FlexibleListConverter<TradierProfileAccount>))]
    public List<TradierProfileAccount>? account { get; set; }
}

public sealed class TradierProfileAccount
{
    public string? account_number { get; set; }
}

public sealed class TradierBalancesResponse
{
    public TradierBalances? balances { get; set; }
}

public sealed class TradierBalances
{
    public string? account_number { get; set; }
    public double? cash { get; set; }
    public double? total_cash { get; set; }
    public double? total_equity { get; set; }
    public double? option_buying_power { get; set; }
    public double? day_pnl { get; set; }
}

public sealed class TradierPositionsResponse
{
    public TradierPositionsData? positions { get; set; }
}

public sealed class TradierPositionsData
{
    [JsonConverter(typeof(FlexibleListConverter<TradierPosition>))]
    public List<TradierPosition>? position { get; set; }
}

public sealed class TradierPosition
{
    public string? symbol { get; set; }
    public long? quantity { get; set; }
    public double? average_cost { get; set; }
    public double? last { get; set; }
}

public sealed class TradierOrdersResponse
{
    public TradierOrdersData? orders { get; set; }
}

public sealed class TradierOrdersData
{
    [JsonConverter(typeof(FlexibleListConverter<TradierOrder>))]
    public List<TradierOrder>? order { get; set; }
}

public sealed class TradierOrder
{
    public object? id { get; set; }
    public string? status { get; set; }
    public string? symbol { get; set; }
    public string? side { get; set; }
    public string? type { get; set; }
    public long? quantity { get; set; }
    public double? price { get; set; }
    public double? avg_fill_price { get; set; }
    public string? created { get; set; }
}

public sealed class TradierOrderResponse
{
    public TradierOrderResult? order { get; set; }
}

public sealed class TradierOrderResult
{
    public object? id { get; set; }
    public string? status { get; set; }
    public string? url { get; set; }
}

[JsonSerializable(typeof(TradierExpirationsResponse))]
[JsonSerializable(typeof(TradierChainResponse))]
[JsonSerializable(typeof(TradierQuotesResponse))]
[JsonSerializable(typeof(TradierSessionRequest))]
[JsonSerializable(typeof(TradierSessionResponse))]
[JsonSerializable(typeof(TradierProfileResponse))]
[JsonSerializable(typeof(TradierBalancesResponse))]
[JsonSerializable(typeof(TradierPositionsResponse))]
[JsonSerializable(typeof(TradierOrdersResponse))]
[JsonSerializable(typeof(TradierOrderResponse))]
public partial class TradierJsonContext : JsonSerializerContext
{
}
