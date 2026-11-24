namespace Berichtsheft.Core.Services;

/// <summary>German public holidays per federal state (ISO codes BW, BY, BE, BB, HB, HH, HE, MV, NI, NW, RP, SL, SN, ST, SH, TH).
/// Only holidays valid in the whole state are included (e.g. not Mariä Himmelfahrt in parts of Bavaria).</summary>
public static class Holidays
{
    public static readonly string[] States = { "BW", "BY", "BE", "BB", "HB", "HH", "HE", "MV", "NI", "NW", "RP", "SL", "SN", "ST", "SH", "TH" };

    public static DateOnly EasterSunday(int year)
    {
        // anonymous Gregorian algorithm (Meeus/Jones/Butcher)
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451, month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }

    /// <summary>Holidays of a year; an empty state gives the nationwide ones only.</summary>
    public static Dictionary<DateOnly, string> GermanHolidays(int year, string? state)
    {
        var s = (state ?? "").Trim().ToUpperInvariant();
        bool In(params string[] states) => states.Contains(s);
        var easter = EasterSunday(year);
        var h = new Dictionary<DateOnly, string>
        {
            [new DateOnly(year, 1, 1)] = "Neujahr",
            [easter.AddDays(-2)] = "Karfreitag",
            [easter.AddDays(1)] = "Ostermontag",
            [new DateOnly(year, 5, 1)] = "Tag der Arbeit",
            [easter.AddDays(39)] = "Christi Himmelfahrt",
            [easter.AddDays(50)] = "Pfingstmontag",
            [new DateOnly(year, 10, 3)] = "Tag der Deutschen Einheit",
            [new DateOnly(year, 12, 25)] = "1. Weihnachtstag",
            [new DateOnly(year, 12, 26)] = "2. Weihnachtstag",
        };
        void Add(DateOnly d, string name) => h.TryAdd(d, name);
        if (In("BW", "BY", "ST")) Add(new DateOnly(year, 1, 6), "Heilige Drei Könige");
        if (In("BE") && year >= 2019 || In("MV") && year >= 2023) Add(new DateOnly(year, 3, 8), "Internationaler Frauentag");
        if (In("BB")) { Add(easter, "Ostersonntag"); Add(easter.AddDays(49), "Pfingstsonntag"); }
        if (In("BW", "BY", "HE", "NW", "RP", "SL")) Add(easter.AddDays(60), "Fronleichnam");
        if (In("SL")) Add(new DateOnly(year, 8, 15), "Mariä Himmelfahrt");
        if (In("TH") && year >= 2019) Add(new DateOnly(year, 9, 20), "Weltkindertag");
        if (In("BB", "MV", "SN", "ST", "TH") || In("HB", "HH", "NI", "SH") && year >= 2018 || year == 2017)
            Add(new DateOnly(year, 10, 31), "Reformationstag");
        if (In("BW", "BY", "NW", "RP", "SL")) Add(new DateOnly(year, 11, 1), "Allerheiligen");
        if (In("SN"))
        {
            var d = new DateOnly(year, 11, 22); // Wednesday before 23 November
            while (d.DayOfWeek != DayOfWeek.Wednesday) d = d.AddDays(-1);
            Add(d, "Buß- und Bettag");
        }
        return h;
    }

    /// <summary>Holidays in the range. Without a federal state there are none (setting "keins").</summary>
    public static Dictionary<DateOnly, string> HolidaysBetween(DateOnly start, DateOnly end, string? state)
    {
        var result = new Dictionary<DateOnly, string>();
        if (string.IsNullOrWhiteSpace(state) || end < start) return result;
        for (var y = start.Year; y <= end.Year; y++)
            foreach (var (d, n) in GermanHolidays(y, state))
                if (d >= start && d <= end) result[d] = n;
        return result;
    }

    /// <summary>Name of a holiday on this date in any state (for reading documents), or null.</summary>
    public static string? AnyStateHolidayName(DateOnly date)
    {
        foreach (var s in States)
            if (GermanHolidays(date.Year, s).TryGetValue(date, out var n)) return n;
        return null;
    }
}
