using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json.Serialization;
using _0dtes_app.Models;
using _0dtes_app.Services;

namespace _0dtes_agent;

/// <summary>
/// Real option chains + greeks from Yahoo Finance's free API.
/// Yahoo's anonymous chain endpoint returns IV (and bid/ask/volume/OI) but not greeks,
/// so delta/gamma/theta/vega are derived via Black-Scholes from the real IV.
/// Uses the cookie + crumb session flow to satisfy Yahoo's consent check.
/// </summary>
public sealed class YahooOptionChainService : IOptionChainService, IQuoteStream, IDisposable
{
    private const string ConsentUrl = "https://fc.yahoo.com";
    private const string CrumbUrl = "https://query1.finance.yahoo.com/v1/test/getcrumb";
    private const string ChainUrl = "https://query2.finance.yahoo.com/v7/finance/options/{0}?date={1}&crumb={2}";
    private const string ChainNoDateUrl = "https://query2.finance.yahoo.com/v7/finance/options/{0}?crumb={1}";

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private readonly YahooQuoteService _quotes = new();
    private readonly object _gate = new();
    private string? _crumb;
    private readonly Dictionary<string, IReadOnlyList<OptionChainRow>> _lastChain = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastFail = new(StringComparer.OrdinalIgnoreCase);
    private const int MinRetryWaitSeconds = 120;

    public string LastFailureReason { get; private set; } = "";

    public YahooOptionChainService()
    {
        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
    }

    public IObservable<QuoteTick> Stream => _quotes.Stream;

    public async Task<QuoteTick?> GetQuoteAsync(string symbol, CancellationToken ct = default)
        => await _quotes.GetQuoteAsync(symbol, ct).ConfigureAwait(false);

    public async Task ConnectAsync(IEnumerable<string> symbols, CancellationToken ct = default)
        => await _quotes.ConnectAsync(symbols, ct).ConfigureAwait(false);

    public Task DisconnectAsync() => _quotes.DisconnectAsync();

    public async Task<IReadOnlyList<OptionExpiry>> GetExpiriesAsync(string symbol, CancellationToken ct = default)
    {
        var root = await FetchRootAsync(symbol, ct).ConfigureAwait(false);
        return (root?.expirationDates ?? new List<long>())
            .Select(UnixTime)
            .Select(d => new OptionExpiry(d))
            .ToList();
    }

    public async Task<IReadOnlyList<OptionChainRow>> GetChainAsync(
        string symbol,
        DateTime expiry,
        double? spot = null,
        CancellationToken ct = default)
    {
        try
        {
            var localOffset = TimeZoneInfo.Local.GetUtcOffset(expiry.Date);
            var date = new DateTimeOffset(DateTime.SpecifyKind(expiry.Date, DateTimeKind.Unspecified), localOffset).ToUnixTimeSeconds();
            var url = string.Format(CultureInfo.InvariantCulture, ChainUrl, Uri.EscapeDataString(symbol), date, await EnsureCrumbAsync(ct).ConfigureAwait(false));
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var response = System.Text.Json.JsonSerializer.Deserialize(
                json, YahooChainJsonContext.Default.YahooOptionChainResponse);
            var option = response?.optionChain?.result?.FirstOrDefault()?.options?.FirstOrDefault();
            if (option is null)
            {
                throw new YahooRateLimitedException("Empty chain response from Yahoo.");
            }

            var current = spot ?? (await GetQuoteAsync(symbol, ct).ConfigureAwait(false))?.Last ?? 0;
            var calls = option.calls ?? new();
            var puts = option.puts ?? new();
            var byStrike = new SortedDictionary<double, (YahooQuoteItem? Call, YahooQuoteItem? Put)>();

            foreach (var c in calls)
            {
                var s = c.strike ?? 0;
                byStrike.TryGetValue(s, out var tuple);
                byStrike[s] = (c, tuple.Put);
            }
            foreach (var p in puts)
            {
                var s = p.strike ?? 0;
                byStrike.TryGetValue(s, out var tuple);
                byStrike[s] = (tuple.Call, p);
            }

            var rows = new List<OptionChainRow>();
            foreach (var (strike, pair) in byStrike)
            {
                rows.Add(new OptionChainRow(
                    strike,
                    pair.Call is { } c2 ? ToQuote(OptionRight.Call, strike, c2, current, expiry) : null,
                    pair.Put is { } p2 ? ToQuote(OptionRight.Put, strike, p2, current, expiry) : null));
            }

            lock (_gate)
            {
                _lastChain[symbol] = rows;
                _lastFail.Remove(symbol);
            }
            LastFailureReason = "";
            return rows;
        }
        catch (YahooRateLimitedException)
        {
            return HandleFailure(symbol);
        }
        catch (Exception ex)
        {
            if (IsRateLimit(ex))
            {
                return HandleFailure(symbol);
            }
            throw;
        }
    }

    private IReadOnlyList<OptionChainRow> HandleFailure(string symbol)
    {
        _crumb = null; // force crumb refresh next attempt
        lock (_gate)
        {
            _lastFail[symbol] = DateTime.Now;
        }
        LastFailureReason = "Yahoo rate-limited; using last good chain.";
        if (_lastChain.TryGetValue(symbol, out var cached) && cached.Count > 0)
        {
            return cached;
        }
        return Array.Empty<OptionChainRow>();
    }

    private static bool IsRateLimit(Exception ex)
    {
        var status = (ex as HttpRequestException)?.StatusCode;
        if (status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.Unauthorized)
        {
            return true;
        }
        return ex.Message.Contains("404", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<YahooOptionResult?> FetchRootAsync(string symbol, CancellationToken ct)
    {
        var url = string.Format(CultureInfo.InvariantCulture, ChainNoDateUrl, Uri.EscapeDataString(symbol), await EnsureCrumbAsync(ct).ConfigureAwait(false));
        var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        return System.Text.Json.JsonSerializer.Deserialize(json, YahooChainJsonContext.Default.YahooOptionChainResponse)?.optionChain?.result?.FirstOrDefault();
    }

    private static OptionQuote ToQuote(OptionRight right, double strike, YahooQuoteItem item, double spot, DateTime expiry)
    {
        var iv = item.impliedVolatility is { } v && double.IsFinite(v) && v > 0 ? v : 0.2;
        var bid = item.bid ?? 0;
        var ask = item.ask ?? 0;
        var last = item.lastPrice;
        var greeks = BlackScholes.Greeks(
            right, spot, strike, expiry, iv, 0.04);
        var volume = item.volume ?? 0;
        var oi = item.openInterest ?? 0;

        return new OptionQuote(
            Math.Round(bid, 2),
            Math.Round(ask, 2),
            last is { } l && l > 0 ? Math.Round(l, 2) : null,
            Math.Round(greeks.Delta, 3),
            Math.Round(greeks.Gamma, 3),
            Math.Round(greeks.Theta / 365.0, 3),
            Math.Round(greeks.Vega, 3),
            Math.Round(iv, 4),
            volume,
            oi);
    }

    private async Task<string> EnsureCrumbAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_crumb is not null)
            {
                return _crumb;
            }
        }

        try
        {
            using var consent = new HttpRequestMessage(HttpMethod.Get, ConsentUrl);
            consent.Version = new Version(2, 0);
            using (await _http.SendAsync(consent, ct).ConfigureAwait(false))
            {
            }
        }
        catch
        {
            // Consent endpoint may 404; cookies are still set in the container.
        }

        using var crumbReq = new HttpRequestMessage(HttpMethod.Get, CrumbUrl);
        crumbReq.Version = new Version(2, 0);
        var crumb = await _http.SendAsync(crumbReq, ct).ConfigureAwait(false);
        if (!crumb.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Yahoo crumb request failed.");
        }
        var text = (await crumb.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();

        lock (_gate)
        {
            _crumb = text;
            return _crumb;
        }
    }

    private static DateTime UnixTime(long seconds)
        => DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;

    public void Dispose() => _http.Dispose();
}

/// <summary>Black-Scholes greeks computed from a real implied volatility.</summary>
public static class BlackScholes
{
    public static (double Delta, double Gamma, double Theta, double Vega) Greeks(
        OptionRight right, double spot, double strike, DateTime expiry, double iv, double rate)
    {
        var t = Math.Max((expiry.Date - DateTime.Today).TotalDays, 1) / 365.0;
        if (iv <= 0 || spot <= 0)
        {
            return (right == OptionRight.Call ? 0.5 : -0.5, 0, 0, 0);
        }

        var d1 = (Math.Log(spot / strike) + (rate + iv * iv / 2) * t) / (iv * Math.Sqrt(t));
        var d2 = d1 - iv * Math.Sqrt(t);
        var nd1 = NormalCdf(d1);
        var pdf = NormalPdf(d1);

        double delta;
        if (right == OptionRight.Call)
        {
            delta = nd1;
        }
        else
        {
            delta = nd1 - 1;
        }

        var gamma = pdf / (spot * iv * Math.Sqrt(t));
        double theta;
        if (right == OptionRight.Call)
        {
            theta = -(spot * pdf * iv) / (2 * Math.Sqrt(t))
                    - rate * strike * Math.Exp(-rate * t) * NormalCdf(d2);
        }
        else
        {
            theta = -(spot * pdf * iv) / (2 * Math.Sqrt(t))
                    + rate * strike * Math.Exp(-rate * t) * NormalCdf(-d2);
        }
        var vega = spot * pdf * Math.Sqrt(t);

        return (delta, gamma, theta, vega / 100.0);
    }

    private static double NormalCdf(double x)
    {
        return 0.5 * (1 + Erf(x / Math.Sqrt(2)));
    }

    private static double NormalPdf(double x)
        => Math.Exp(-0.5 * x * x) / Math.Sqrt(2 * Math.PI);

    private static double Erf(double x)
    {
        // Abramowitz-Stegun approximation.
        var sign = x < 0 ? -1.0 : 1.0;
        x = Math.Abs(x);
        var t = 1.0 / (1.0 + 0.3275911 * x);
        var y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return sign * y;
    }
}

public sealed class YahooOptionChainResponse
{
    public YahooOptionChain? optionChain { get; set; }
}

public sealed class YahooOptionChain
{
    public List<YahooOptionResult>? result { get; set; }
}

public sealed class YahooOptionResult
{
    public List<long>? expirationDates { get; set; }
    public List<YahooOptionDate>? options { get; set; }
}

public sealed class YahooOptionDate
{
    public List<YahooQuoteItem>? calls { get; set; }
    public List<YahooQuoteItem>? puts { get; set; }
}

public sealed class YahooQuoteItem
{
    public double? strike { get; set; }
    public double? bid { get; set; }
    public double? ask { get; set; }
    public double? lastPrice { get; set; }
    public long? volume { get; set; }
    public long? openInterest { get; set; }
    public double? impliedVolatility { get; set; }
}

[JsonSerializable(typeof(YahooOptionChainResponse))]
internal partial class YahooChainJsonContext : JsonSerializerContext
{
}

public sealed class YahooRateLimitedException : Exception
{
    public YahooRateLimitedException(string message) : base(message)
    {
    }
}
