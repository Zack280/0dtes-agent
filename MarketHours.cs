namespace _0dtes_agent;

/// <summary>
/// Determines whether the NYSE is currently in its regular session
/// (Mon-Fri 09:30-16:00 US Eastern, on non-holidays).
/// </summary>
public static class MarketHours
{
    private static readonly HashSet<DateTime> Holidays = BuildHolidays();

    public static bool IsOpen(DateTime localTime)
    {
        var et = ToEastern(localTime);
        if (et.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }
        if (Holidays.Contains(et.Date))
        {
            return false;
        }
        var t = et.Hour + et.Minute / 60.0;
        return t is >= 9.5 and < 16.0;
    }

    /// <summary>Convert a local (machine) time to US Eastern, handling DST.</summary>
    public static DateTime ToEastern(DateTime local)
    {
        var utc = TimeZoneInfo.Local.IsInvalidTime(local)
            ? DateTime.SpecifyKind(local, DateTimeKind.Utc)
            : local.ToUniversalTime();
        return FromUtc(utc);
    }

    public static DateTime FromUtc(DateTime utc)
    {
        var isDst = IsEasternDst(utc);
        var offset = isDst ? -4 : -5;
        return DateTime.SpecifyKind(utc.AddHours(offset), DateTimeKind.Unspecified);
    }

    /// <summary>
    /// US DST starts second Sunday in March, ends first Sunday in November.
    /// </summary>
    private static bool IsEasternDst(DateTime utc)
    {
        if (utc.Month < 3 || utc.Month > 11)
        {
            return false;
        }
        if (utc.Month < 11)
        {
            return utc.Month > 3;
        }
        var start = SecondSunday(utc.Year, 3);
        var end = FirstSunday(utc.Year, 11);
        return utc >= start && utc < end;
    }

    private static DateTime SecondSunday(int year, int month)
    {
        var first = FirstSunday(year, month);
        return first.AddDays(7);
    }

    private static DateTime FirstSunday(int year, int month)
    {
        var day = 1;
        var date = new DateTime(year, month, 1);
        while (date.DayOfWeek != DayOfWeek.Sunday)
        {
            date = date.AddDays(1);
            day++;
        }
        return date;
    }

    private static HashSet<DateTime> BuildHolidays()
    {
        var set = new HashSet<DateTime>();
        for (var year = 2025; year <= 2030; year++)
        {
            Add(new DateTime(year, 1, 1));                        // New Year's
            Add(NthWeekday(year, 1, DayOfWeek.Monday, 3));        // MLK
            Add(NthWeekday(year, 2, DayOfWeek.Monday, 3));        // Presidents
            Add(LastWeekday(year, 5, DayOfWeek.Monday));          // Memorial
            Add(new DateTime(year, 6, 19));                       // Juneteenth
            Add(new DateTime(year, 7, 4));                        // Independence
            Add(NthWeekday(year, 9, DayOfWeek.Monday, 1));        // Labor
            Add(NthWeekday(year, 11, DayOfWeek.Thursday, 4));     // Thanksgiving
            Add(new DateTime(year, 12, 25));                      // Christmas
        }

        void Add(DateTime d) => set.Add(d);
        return set;
    }

    private static DateTime NthWeekday(int year, int month, DayOfWeek dow, int n)
    {
        var d = new DateTime(year, month, 1);
        while (d.DayOfWeek != dow)
        {
            d = d.AddDays(1);
        }
        return d.AddDays(7 * (n - 1));
    }

    private static DateTime LastWeekday(int year, int month, DayOfWeek dow)
    {
        var d = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        while (d.DayOfWeek != dow)
        {
            d = d.AddDays(-1);
        }
        return d;
    }
}