using System.Globalization;
using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Generation;

namespace Berichtsheft.Core.Services;

/// <summary>Every week of the training with its report state (see ARCHITECTURE.md, "Missing weeks").</summary>
public static class CoverageService
{
    /// <summary>Ausbildungsjahr ("1", "2", …) of a date; "" without a training start.</summary>
    public static string ComputeTrainingYear(DateOnly? trainingStart, DateOnly date)
    {
        if (trainingStart is not { } ts) return "";
        return TrainingYearNumber(ts, date).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>1 in the first twelve months after the start, 2 in the next twelve, …; at least 1.</summary>
    public static int TrainingYearNumber(DateOnly trainingStart, DateOnly date)
    {
        if (date < trainingStart) return 1;
        var years = date.Year - trainingStart.Year;
        if (date < trainingStart.AddYears(years)) years--;
        return Math.Max(1, years + 1);
    }

    /// <param name="reports">Monday of the week → (report id, status, number).</param>
    /// <param name="done">Statuses that count as finished (not overdue).</param>
    public static CoverageOut ComputeCoverage(DateOnly? trainingStart, DateOnly? trainingEnd, DateOnly today,
        Dictionary<DateOnly, (int Id, string Status, string Number)> reports, HashSet<string> done, int overdueDays, DateOnly? firstReport)
    {
        var outp = new CoverageOut();
        var begin = trainingStart ?? firstReport;
        if (begin == null)
        {
            outp.Hint = "Bitte in den Einstellungen den Ausbildungsbeginn eintragen – dann zeigt die Übersicht alle Wochen der Ausbildung.";
            return outp;
        }
        var first = Scheduling.WeekBounds(begin.Value).Monday;
        var currentWeek = Scheduling.WeekBounds(today).Monday;
        var last = currentWeek;
        if (trainingEnd is { } te && Scheduling.WeekBounds(te).Monday < last) last = Scheduling.WeekBounds(te).Monday;
        if (reports.Count > 0 && reports.Keys.Max() > last) last = reports.Keys.Max(); // reports written in advance
        outp.StartDate = first;
        outp.EndDate = last.AddDays(4);

        for (var monday = first; monday <= last; monday = monday.AddDays(7))
        {
            var friday = monday.AddDays(4);
            var dt = monday.ToDateTime(TimeOnly.MinValue);
            var w = new CoverageWeek
            {
                StartDate = monday, EndDate = friday, CalendarWeek = ISOWeek.GetWeekOfYear(dt), Year = ISOWeek.GetYear(dt),
                TrainingYear = ComputeTrainingYear(trainingStart, monday),
            };
            if (reports.TryGetValue(monday, out var r))
            {
                w.ReportId = r.Id;
                w.Status = r.Status;
                w.ReportNumber = r.Number;
                w.Overdue = !done.Contains(r.Status) && today.DayNumber > friday.DayNumber + overdueDays;
            }
            else
            {
                w.Status = monday >= currentWeek ? "current" : "missing";
                w.ReportNumber = ReportService.ComputeReportNumber(trainingStart, monday);
                w.Overdue = w.Status == "missing" && today.DayNumber > friday.DayNumber + overdueDays;
            }
            outp.Weeks.Add(w);
        }
        foreach (var g in outp.Weeks.GroupBy(w => w.Status)) outp.Counts[g.Key] = g.Count();
        outp.Missing = outp.Weeks.Count(w => w.Status == "missing");
        outp.Overdue = outp.Weeks.Count(w => w.Overdue);
        return outp;
    }
}
