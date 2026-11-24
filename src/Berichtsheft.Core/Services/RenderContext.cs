using Berichtsheft.Core.Generation;
using Berichtsheft.Core.Models;
using Berichtsheft.Core.Templating;

namespace Berichtsheft.Core.Services;

/// <summary>Header data of a report for rendering.</summary>
public sealed class ReportRenderInfo
{
    public string TraineeName { get; set; } = "";
    public string ReportNumber { get; set; } = "";
    public int CalendarWeek { get; set; }
    public int Year { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string DepartmentLabel { get; set; } = "";
    public string TrainingYear { get; set; } = "";
    public string Remarks { get; set; } = "";
}

/// <summary>Builds the placeholder values of a report (see docs/TEMPLATES.md for the list).</summary>
public static class RenderContext
{
    public const int DefaultLineChars = 95;
    private static readonly string[] Prefix = { "SO", "MO", "DI", "MI", "DO", "FR", "SA" };

    public static string DayPrefix(DateOnly d) => Prefix[(int)d.DayOfWeek];

    public static (Dictionary<string, object> Values, List<string> Warnings) BuildValues(
        ReportRenderInfo report, List<ReportDay> days, AppSettings cfg, Dictionary<string, int> capacity,
        byte[]? signaturePng = null, DateTime? signedAt = null, string signerName = "",
        byte[]? trainerPng = null, DateTime? trainerSignedAt = null, string trainerName = "",
        int? lineChars = null)
    {
        var width = lineChars is > 0 ? lineChars.Value : DefaultLineChars;
        var warnings = new List<string>();
        var v = new Dictionary<string, object>
        {
            ["NAME"] = report.TraineeName ?? "",
            ["NR"] = report.ReportNumber ?? "",
            ["KW"] = report.CalendarWeek.ToString(),
            ["YEAR"] = report.Year.ToString(),
            ["START_DATE"] = report.StartDate.ToString("dd.MM.yyyy"),
            ["END_DATE"] = report.EndDate.ToString("dd.MM.yyyy"),
            ["PERIOD"] = $"{report.StartDate:dd.MM.yyyy} – {report.EndDate:dd.MM.yyyy}",
            ["DATE"] = DateTime.Today.ToString("dd.MM.yyyy"),
            ["DEPARTMENT"] = report.DepartmentLabel ?? "",
            ["TRAINING_YEAR"] = report.TrainingYear ?? "",
            ["COMPANY"] = cfg.Company ?? "",
            ["PROFESSION"] = cfg.Profession ?? "",
            ["REMARKS"] = report.Remarks ?? "",
            ["TOTAL_HOURS"] = Scheduling.FormatHours(days.SelectMany(d => d.Entries).Sum(e => e.Hours ?? 0)),
            ["ENTRIES"] = days.SelectMany(d => d.Entries).Where(e => e.Text.Trim().Length > 0)
                .Select(e => e.Hours is { } h ? $"{e.Text} ({Scheduling.FormatHours(h)} Std.)" : e.Text).ToList(),
            ["SIGNATURE"] = signaturePng is { Length: > 0 } ? new ImageValue(signaturePng) : "",
            ["SIGNATURE_DATE"] = signaturePng is { Length: > 0 } && signedAt is { } sa ? sa.ToString("dd.MM.yyyy") : "",
            ["SIGNER_NAME"] = signaturePng is { Length: > 0 } ? signerName ?? "" : "",
            ["TRAINER_SIGNATURE"] = trainerPng is { Length: > 0 } ? new ImageValue(trainerPng) : "",
            ["TRAINER_SIGNATURE_DATE"] = trainerPng is { Length: > 0 } && trainerSignedAt is { } ta ? ta.ToString("dd.MM.yyyy") : "",
            ["TRAINER_NAME"] = trainerPng is { Length: > 0 } ? trainerName ?? "" : "",
        };

        // days the template has lines for, but the report does not: blank lines
        foreach (var (prefix, cap) in capacity)
            for (var i = 1; i <= cap; i++) { v[$"{prefix}_{i}"] = ""; v[$"{prefix}_{i}_H"] = ""; }

        foreach (var day in days)
        {
            var p = DayPrefix(day.Date);
            v[$"{p}_DATE"] = day.Date.ToString("dd.MM.yyyy");
            v[$"{p}_HOURS"] = Scheduling.FormatHours(day.Entries.Sum(e => e.Hours ?? 0) is var s && s > 0 ? s : null);
            v[$"{p}_TYPE"] = Scheduling.DayTypes.GetValueOrDefault(day.Type, day.Type);
            var cap = capacity.TryGetValue(p, out var c) ? c : int.MaxValue;
            var lines = Layout(day.Entries.Where(e => e.Text.Trim().Length > 0 || e.Hours != null).ToList(), cap, width, out var merged);
            if (merged > 0)
                warnings.Add($"{day.Date:dd.MM.}: Der Tag braucht mehr als die {cap} Zeilen der Vorlage – {merged} Einträge wurden in der letzten Zeile zusammengefasst.");
            for (var i = 0; i < lines.Count; i++)
            {
                v[$"{p}_{i + 1}"] = lines[i].Text;
                v[$"{p}_{i + 1}_H"] = Scheduling.FormatHours(lines[i].Hours);
            }
        }
        return (v, warnings);
    }

    /// <summary>Wraps each entry onto form lines (hours on the first line). If a day needs more lines than the
    /// template has, the entries that do not fit are joined with "; " into the last line.</summary>
    private static List<(string Text, double? Hours)> Layout(List<ReportEntry> entries, int cap, int width, out int merged)
    {
        merged = 0;
        var wrapped = entries.Select(e => (Lines: LineWrap.WrapText(e.Text, width), e.Hours)).ToList();
        var outp = new List<(string, double?)>();
        if (wrapped.Sum(w => w.Lines.Count) <= cap)
        {
            foreach (var (lines, hours) in wrapped)
                for (var i = 0; i < lines.Count; i++) outp.Add((lines[i], i == 0 ? hours : null));
            return outp;
        }
        var k = 0;
        for (; k < wrapped.Count && outp.Count + wrapped[k].Lines.Count <= cap - 1; k++)
            for (var i = 0; i < wrapped[k].Lines.Count; i++) outp.Add((wrapped[k].Lines[i], i == 0 ? wrapped[k].Hours : null));
        var rest = entries.Skip(k).ToList();
        merged = rest.Count;
        var sum = rest.Sum(e => e.Hours ?? 0);
        outp.Add((string.Join("; ", rest.Select(e => e.Text.Trim())), sum > 0 ? sum : null));
        return outp;
    }
}
