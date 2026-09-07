namespace _0dtes_agent;

/// <summary>
/// US Eastern time conversion without TimeZoneInfo lookups, so it behaves the
/// same on Windows (local runs) and Linux (GitHub Actions). DST starts on the
/// second Sunday of March and ends on the first Sunday of November, matching
/// the ET logic in docs/index.html.
/// </summary>
public static class UsEastern
{
    private static readonly TimeSpan EstOffset = TimeSpan.FromHours(-5);
    private static readonly TimeSpan EdtOffset = TimeSpan.FromHours(-4);

    public static DateTime ToLocal(DateTime utc)
        => DateTime.SpecifyKind(utc.ToUniversalTime() + (IsDst(utc.ToUniversalTime()) ? EdtOffset : EstOffset), DateTimeKind.Unspecified);

    /// <summary>ET hour (0-23) of the given UTC instant.</summary>
    public static int Hour(DateTime utc)
        => ToLocal(utc).Hour;

    public static bool IsDst(DateTime utc)
    {
        var utcDay = utc.Date;
        var start = NthSunday(utc.Year, 3, 2);
        var end = NthSunday(utc.Year, 11, 1);
        return utcDay >= start && utcDay < end;
    }

    private static DateTime NthSunday(int year, int month, int n)
    {
        var first = new DateTime(year, month, 1);
        var firstSunday = first.AddDays(((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7);
        return firstSunday.AddDays((n - 1) * 7);
    }
}