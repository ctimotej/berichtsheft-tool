using System.Text.RegularExpressions;
using Berichtsheft.Core.Dtos;

namespace Berichtsheft.Core.Services;

/// <summary>Lernfelder of the IT professions (Fachinformatiker, KMK framework curriculum 2020) and which of them belong to
/// which Ausbildungsjahr: LF1–LF5 in the 1st, LF6–LF9 in the 2nd and LF10–LF12 of the own Fachrichtung in the 3rd year.
/// A subject is recognised by its Kürzel: "LF3", "LF 3", "LF10b" …; the letter is the Fachrichtung (a–d).</summary>
public static class Curriculum
{
    public sealed record Specialization(char Letter, string Name, string Profession);

    public static readonly IReadOnlyList<Specialization> Specializations = new[]
    {
        new Specialization('a', "Anwendungsentwicklung", "Fachinformatiker für Anwendungsentwicklung"),
        new Specialization('b', "Systemintegration", "Fachinformatiker für Systemintegration"),
        new Specialization('c', "Daten- und Prozessanalyse", "Fachinformatiker für Daten- und Prozessanalyse"),
        new Specialization('d', "Digitale Vernetzung", "Fachinformatiker für Digitale Vernetzung"),
    };

    private static readonly Regex CodeRx = new(@"^\s*(?:LF|Lernfeld)\s*0*(\d{1,2})\s*([a-d])?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DescSuffixRx = new(@"\s*\([^()]*Ausbildungsjahr[^()]*\)\s*$", RegexOptions.Compiled);

    /// <summary>Fachrichtung from the profession in the settings ("Fachinformatiker für Systemintegration" → b), or null.</summary>
    public static Specialization? SpecializationOf(string? profession)
    {
        var p = (profession ?? "").ToLowerInvariant();
        if (p.Length == 0) return null;
        if (p.Contains("anwendungsentwicklung")) return Specializations[0];
        if (p.Contains("systemintegration")) return Specializations[1];
        if (p.Contains("prozessanalyse")) return Specializations[2];
        if (p.Contains("vernetzung")) return Specializations[3];
        return null;
    }

    /// <summary>Lernfeld number and Fachrichtung letter from a Kürzel (or name) like "LF10b"; null for other subjects.</summary>
    public static (int Number, char? Variant)? Parse(string? codeOrName)
    {
        var m = CodeRx.Match(codeOrName ?? "");
        if (!m.Success) return null;
        var n = int.Parse(m.Groups[1].Value);
        return (n, m.Groups[2].Success ? char.ToLowerInvariant(m.Groups[2].Value[0]) : null);
    }

    public static (int Number, char? Variant)? Parse(DepartmentOut d) => Parse(d.Code) ?? Parse(d.Name);

    /// <summary>1 for LF1–LF5, 2 for LF6–LF9, 3 for LF10–LF12, null otherwise.</summary>
    public static int? YearOfLernfeld(int number) => number switch
    {
        >= 1 and <= 5 => 1,
        >= 6 and <= 9 => 2,
        >= 10 and <= 12 => 3,
        _ => null,
    };

    /// <summary>The Lernfelder for an Ausbildungsjahr. In the 3rd year only those of the Fachrichtung (when it is known);
    /// later years (extended training) use the 3rd year's. Ordered by Lernfeld number.</summary>
    public static List<DepartmentOut> ForYear(IEnumerable<DepartmentOut> subjects, int trainingYear, Specialization? spec)
    {
        var year = Math.Clamp(trainingYear, 1, 3);
        return subjects
            .Select(d => (d, p: Parse(d)))
            .Where(x => x.p is { } p && YearOfLernfeld(p.Number) == year
                        && (p.Variant == null || spec == null || p.Variant == spec.Letter))
            .OrderBy(x => x.p!.Value.Number).ThenBy(x => x.p!.Value.Variant)
            .Select(x => x.d).ToList();
    }

    private static readonly Regex HoursRx = new(@"(\d+)\s*Std", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Lesson hours of a Lernfeld from its description ("… (1. Ausbildungsjahr, 80 Std.)"); 80 if not given.</summary>
    public static int HoursOf(DepartmentOut d) =>
        HoursRx.Match(d.Description ?? "") is { Success: true } m && int.Parse(m.Groups[1].Value) is > 0 and var h ? h : 80;

    /// <summary>Overlap around the week (one month each side): around a change of Lernfeld both are taught.</summary>
    public const double OverlapShare = 1.0 / 12;

    /// <summary>The Lernfelder taught at <paramref name="date"/>. The Lernfelder of the Ausbildungsjahr follow each other in the
    /// order of their numbers, each taking a share of the year proportional to its hours (1st year: LF1 40 h = first eighth,
    /// LF2 80 h = next quarter, …). Near a change both Lernfelder are returned. In years after the 3rd (extended training)
    /// all Lernfelder of the 3rd year are returned.</summary>
    public static List<DepartmentOut> ForDate(IEnumerable<DepartmentOut> subjects, DateOnly trainingStart, DateOnly date, Specialization? spec)
    {
        var year = CoverageService.TrainingYearNumber(trainingStart, date);
        var list = ForYear(subjects, year, spec);
        if (year > 3 || list.Count <= 1) return list;

        var yearStart = trainingStart.AddYears(year - 1);
        var yearEnd = trainingStart.AddYears(year);
        var pos = Math.Clamp((date.DayNumber - yearStart.DayNumber) / (double)(yearEnd.DayNumber - yearStart.DayNumber), 0, 1);

        // one slot per Lernfeld number (without a Fachrichtung LF10a–d share the slot of LF10)
        var slots = list.GroupBy(d => Parse(d)!.Value.Number).OrderBy(g => g.Key)
            .Select(g => (Number: g.Key, Hours: g.Max(HoursOf))).ToList();
        var total = (double)slots.Sum(s => s.Hours);
        var active = new HashSet<int>();
        var from = 0.0;
        foreach (var (number, hours) in slots)
        {
            var to = from + hours / total;
            if (from < pos + OverlapShare && to > pos - OverlapShare) active.Add(number);
            from = to;
        }
        return list.Where(d => active.Contains(Parse(d)!.Value.Number)).ToList();
    }

    /// <summary>Sort key so that LF2 comes before LF10 and other subjects after the Lernfelder.</summary>
    public static (int, int, string) SortKey(DepartmentOut d) =>
        Parse(d) is { } p ? (0, p.Number * 10 + (p.Variant is { } v ? v - 'a' + 1 : 0), "") : (1, 0, d.Name.ToLowerInvariant());

    /// <summary>"LF10b: Serverdienste bereitstellen …" – title from the name, or from the description when the name is just the Kürzel.</summary>
    public static string Label(DepartmentOut d)
    {
        var name = d.Name.Trim();
        var code = d.Code.Trim();
        if (code.Length == 0) return name;
        if (!name.Equals(code, StringComparison.OrdinalIgnoreCase))
            return name.StartsWith(code, StringComparison.OrdinalIgnoreCase) ? name : $"{code}: {name}";
        var title = DescSuffixRx.Replace(d.Description ?? "", "").Trim();
        return title.Length > 0 ? $"{code}: {title}" : code;
    }
}
