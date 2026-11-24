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
