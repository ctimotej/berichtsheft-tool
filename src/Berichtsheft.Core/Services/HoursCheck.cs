using Berichtsheft.Core.Generation;
using Berichtsheft.Core.Models;

namespace Berichtsheft.Core.Services;

public sealed record HoursIssue(int DayIndex, DateOnly Date, double Actual, double Expected, string Message);

/// <summary>Days whose hours do not add up to the working hours from the settings (work and school days).</summary>
public static class HoursCheck
{
    private static readonly string[] Weekdays = { "So", "Mo", "Di", "Mi", "Do", "Fr", "Sa" };

    public static double? Expected(string type, AppSettings cfg) => type switch
    {
        "work" => cfg.HoursPerDay,
        "school" => cfg.SchoolHoursPerDay,
        _ => null,
    };

    public static List<HoursIssue> Check(IReadOnlyList<ReportDay> days, AppSettings cfg)
    {
        var issues = new List<HoursIssue>();
        for (var i = 0; i < days.Count; i++)
        {
            var d = days[i];
            if (Expected(d.Type, cfg) is not { } expected || expected <= 0) continue;
            var entries = d.Entries.Where(e => e.Text.Trim().Length > 0 || e.Hours != null).ToList();
            if (entries.Count == 0) continue;
            var label = $"{Weekdays[(int)d.Date.DayOfWeek]} {d.Date:dd.MM.}";
            if (entries.All(e => e.Hours == null))
            {
                issues.Add(new HoursIssue(i, d.Date, 0, expected, $"{label}: keine Stunden eingetragen (Soll {Scheduling.FormatHours(expected)})"));
                continue;
            }
            var sum = entries.Sum(e => e.Hours ?? 0);
            if (Math.Abs(sum - expected) > 0.01)
                issues.Add(new HoursIssue(i, d.Date, sum, expected, $"{label}: {Scheduling.FormatHours(sum)} Std. statt {Scheduling.FormatHours(expected)}"));
        }
        return issues;
    }

    /// <summary>Spreads the expected hours evenly (0.5 steps) over the entries of the given days.</summary>
    public static void Redistribute(IReadOnlyList<ReportDay> days, AppSettings cfg, IEnumerable<int> dayIndexes)
    {
        foreach (var i in dayIndexes.Distinct())
        {
            if (i < 0 || i >= days.Count) continue;
            var d = days[i];
            if (Expected(d.Type, cfg) is not { } expected) continue;
            var entries = d.Entries.Where(e => e.Text.Trim().Length > 0 || e.Hours != null).ToList();
            if (entries.Count == 0) continue;
            var hours = Scheduling.DistributeHours(expected, entries.Count);
            for (var k = 0; k < entries.Count; k++) entries[k].Hours = hours[k] > 0 ? hours[k] : null;
        }
    }
}
