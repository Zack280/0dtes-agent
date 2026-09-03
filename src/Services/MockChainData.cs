namespace _0dtes_app.Services;

public static class MockChainData
{
    public static double DefaultSpot(string symbol) => string.Equals(symbol, "SPY", StringComparison.OrdinalIgnoreCase) ? 512.34 : 12.50;

    public static IReadOnlyList<OptionExpiry> Expiries(DateTime from)
    {
        var result = new List<OptionExpiry>();
        var date = from;
        while (result.Count < 5)
        {
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                result.Add(new OptionExpiry(date));
            }
            date = date.AddDays(1);
        }
        return result;
    }

    public static IReadOnlyList<OptionChainRow> BuildChain(double spot, DateTime expiry, double iv = 0.16)
    {
        var rows = new List<OptionChainRow>();
        var start = Math.Floor(spot - 10);
        var end = Math.Ceiling(spot + 10);
        for (var strike = start; strike <= end; strike += 1)
        {
            rows.Add(new OptionChainRow(
                strike,
                Quote(OptionRight.Call, spot, strike, iv, expiry),
                Quote(OptionRight.Put, spot, strike, iv, expiry)));
        }
        return rows;
    }

    public static OptionQuote Quote(OptionRight right, double spot, double strike, double iv, DateTime expiry)
    {
        var hoursRemaining = Math.Max((expiry.Date - DateTime.Today).TotalHours + 3, 0.5);
        var tYears = hoursRemaining / 8760.0;
        var sigmaSqrt = iv * Math.Sqrt(tYears);
        var atm = 0.4 * spot * sigmaSqrt;
        var scaled = (strike - spot) / spot;

        double price;
        if (right == OptionRight.Call)
        {
            price = scaled <= 0
                ? (spot - strike) + atm * Math.Exp(-8 * Math.Abs(scaled))
                : atm * Math.Exp(-45 * scaled);
        }
        else
        {
            price = scaled >= 0
                ? (strike - spot) + atm * Math.Exp(-8 * Math.Abs(scaled))
                : atm * Math.Exp(45 * scaled);
        }
        price = Math.Max(price, 0.01);

        var spread = Math.Max(0.03, price * 0.12);
        var bid = Math.Max(0.01, price - spread / 2);
        var ask = price + spread / 2;

        var callDelta = Math.Clamp(0.5 - 2.5 * scaled, 0.02, 0.98);
        var delta = right == OptionRight.Call ? callDelta : callDelta - 1;
        var gamma = 0.035 * Math.Exp(-12 * scaled * scaled) + 0.004;
        var theta = -Math.Max(price * 0.35, 0.01);
        var vega = 0.02 * Math.Exp(-6 * scaled * scaled) + 0.004;
        var ivQuoted = iv * (1 + 40 * Math.Abs(scaled));
        var volume = (long)(15000 * Math.Exp(-150 * Math.Abs(scaled)) + 60);
        var oi = (long)(20000 * Math.Exp(-40 * Math.Abs(scaled)) + 400);

        return new OptionQuote(
            Math.Round(bid, 2),
            Math.Round(ask, 2),
            Math.Round(price, 2),
            Math.Round(delta, 3),
            Math.Round(gamma, 3),
            Math.Round(theta, 3),
            Math.Round(vega, 3),
            Math.Round(ivQuoted, 4),
            volume,
            oi);
    }
}
