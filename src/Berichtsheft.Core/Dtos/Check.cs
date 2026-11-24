using System.Collections;
using System.Globalization;

namespace Berichtsheft.Core.Dtos;

/// <summary>Input validation helpers. Field names are the snake_case names of the DTO properties;
/// messages are German and shown to the user.</summary>
public static class Check
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["name"] = "Name", ["text"] = "Text", ["code"] = "Kürzel", ["description"] = "Beschreibung", ["category"] = "Kategorie",
        ["trainee_name"] = "Name", ["company"] = "Ausbildungsbetrieb", ["profession"] = "Beruf", ["report_number"] = "Nachweis-Nr.",
        ["training_year"] = "Ausbildungsjahr", ["department_label"] = "Abteilung", ["remarks"] = "Bemerkungen", ["note"] = "Notiz",
        ["count"] = "Anzahl der Einträge", ["count_max"] = "Höchstzahl der Einträge", ["hours_per_day"] = "Stunden pro Arbeitstag",
        ["school_hours_per_day"] = "Stunden pro Schultag", ["default_count"] = "Anzahl der Einträge", ["default_count_max"] = "Höchstzahl der Einträge",
        ["avoid_recent_reports"] = "Themen der letzten Berichte meiden", ["school_entries_per_day"] = "Einträge pro Schultag",
        ["overdue_days"] = "Tage bis überfällig", ["line_chars"] = "Zeichen pro Zeile", ["department_ids"] = "Themen",
        ["school_department_ids"] = "Lernfelder", ["subject_ids"] = "Lernfelder", ["topic_ids"] = "Themen",
    };

    private static string Label(string field)
    {
        var last = field.Split('.').Last();
        return Labels.TryGetValue(last, out var l) ? l : field;
    }

    public static InvalidInputError Fail(string field, string message) =>
        new(field.Length > 0 ? $"{Label(field)}: {message}" : message, field);

    public static void Str(string field, string? value, int min, int max)
    {
        var len = (value ?? "").Trim().Length;
        if (len < min) throw Fail(field, min <= 1 ? "darf nicht leer sein." : $"mindestens {min} Zeichen.");
        if ((value ?? "").Length > max) throw Fail(field, $"höchstens {max} Zeichen.");
    }

    public static void Num(string field, double? value, double min, double max)
    {
        if (value is not { } v) return;
        if (double.IsNaN(v) || v < min || v > max)
            throw Fail(field, $"muss zwischen {Fmt(min)} und {Fmt(max)} liegen.");
    }

    public static void Count(string field, ICollection? items, int min, int max)
    {
        var n = items?.Count ?? 0;
        if (n < min) throw Fail(field, min == 1 ? "bitte mindestens eins auswählen." : $"mindestens {min} nötig.");
        if (n > max) throw Fail(field, $"höchstens {max} erlaubt.");
    }

    /// <summary>Empty values pass (required fields are checked separately).</summary>
    public static void OneOf(string field, string? value, params string[] allowed)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (!allowed.Contains(value)) throw Fail(field, $"ungültiger Wert „{value}“.");
    }

    private static string Fmt(double d) => d.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',');
}
