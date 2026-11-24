using System.Globalization;

namespace Berichtsheft.Core.Generation;

/// <summary>Week bounds, distribution of entries and hours over days.</summary>
public static class Scheduling
{
    /// <summary>Default line text of a day without generated entries.</summary>
    public static readonly IReadOnlyDictionary<string, string> DayTypes = new Dictionary<string, string>
    {
        ["work"] = "Betrieb", ["school"] = "Berufsschule", ["vacation"] = "Urlaub",
        ["holiday"] = "Feiertag", ["sick"] = "Krank", ["free"] = "Frei",
    };

    /// <summary>Monday and Friday of the ISO week of <paramref name="day"/>.</summary>
    public static (DateOnly Monday, DateOnly Friday) WeekBounds(DateOnly day)
    {
        var offset = ((int)day.DayOfWeek + 6) % 7; // Monday = 0
        var monday = day.AddDays(-offset);
        return (monday, monday.AddDays(4));
    }

    /// <summary>Splits <paramref name="total"/> entries over <paramref name="days"/> days, larger counts first.</summary>
    public static List<int> SplitCounts(int total, int days)
    {
        if (days <= 0) return new();
        var b = total / days;
        var rem = total % days;
        return Enumerable.Range(0, days).Select(i => b + (i < rem ? 1 : 0)).ToList();
    }

    /// <summary>Per day a random count between <paramref name="min"/> and <paramref name="max"/>; with more than
    /// one day both limits occur at least once. <paramref name="max"/> ≤ <paramref name="min"/> means always <paramref name="min"/>.</summary>
    public static List<int> RangeCounts(int min, int max, int days, Random rnd)
    {
        if (days <= 0) return new();
        if (max <= min) return Enumerable.Repeat(min, days).ToList();
        var counts = Enumerable.Range(0, days).Select(_ => rnd.Next(min, max + 1)).ToList();
        if (days > 1)
        {
            var positions = Enumerable.Range(0, days).OrderBy(_ => rnd.Next()).Take(2).ToList();
            counts[positions[0]] = min;
            counts[positions[1]] = max;
        }
        return counts;
    }

    /// <summary>Spreads <paramref name="total"/> hours over <paramref name="n"/> entries in 0.5 steps; the sum is exact.</summary>
    public static List<double> DistributeHours(double total, int n)
    {
        if (n <= 0) return new();
        if (total <= 0) return Enumerable.Repeat(0.0, n).ToList();
        var halves = (int)Math.Floor(total * 2 + 1e-9);
        var b = halves / n;
        var rem = halves % n;
        var hours = Enumerable.Range(0, n).Select(i => (b + (i < rem ? 1 : 0)) / 2.0).ToList();
        var diff = Math.Round(total - hours.Sum(), 6); // e.g. 7.75: the quarter goes to the first entry
        if (diff != 0) hours[0] = Math.Round(hours[0] + diff, 6);
        return hours;
    }

    /// <summary>German number format without trailing zeros: 2,5 / 3; empty for null.</summary>
    public static string FormatHours(double? hours)
    {
        if (hours is not { } h) return "";
        return Math.Round(h, 2).ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',');
    }
}
