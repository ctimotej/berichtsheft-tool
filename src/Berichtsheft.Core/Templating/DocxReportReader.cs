using System.Globalization;
using System.Text.RegularExpressions;
using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Models;
using Berichtsheft.Core.Services;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Berichtsheft.Core.Templating;

/// <summary>Reads a filled-in Ausbildungsnachweis (the usual form: header fields, one block of lines per weekday,
/// hours column) back into an <see cref="ImportWeek"/>. Works with documents made by this program and with forms
/// filled in by hand in Word, as long as they use the same layout.</summary>
public static class DocxReportReader
{
    private static readonly string[] WeekdayNames = { "montag", "dienstag", "mittwoch", "donnerstag", "freitag" };
    private static readonly Regex DateRx = new(@"(\d{1,2})\.(\d{1,2})\.(\d{2,4})", RegexOptions.Compiled);
    private static readonly Regex SchoolLineRx = new(@"^(LF\s?\d+[a-d]?|Lernfeld\s?\d+)\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>Approximate characters per form line; a line that could not take the next word was wrapped.</summary>
    private const int LineChars = 95;

    public static ImportWeek Read(byte[] docx, string sourceName)
    {
        WordprocessingDocument doc;
        try { doc = WordprocessingDocument.Open(new MemoryStream(docx), false); }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            throw new TemplateError("Die Datei ist kein lesbares Word-Dokument (.docx).", exc);
        }
        using (doc)
        {
            var body = doc.MainDocumentPart?.Document?.Body ?? throw new TemplateError("Die Datei enthält kein Word-Dokument.");
            var week = ImportWeek.Blank();
            week.Source = sourceName;
            var rows = body.Descendants<TableRow>().ToList();
            ReadHeader(rows, week);
            ReadDays(rows, week);
            ReadRemarks(body, week);
            if (week.StartDate == null) week.Warnings.Add("Das Datum der Woche wurde nicht erkannt.");
            else if (week.StartDate.Value.DayOfWeek != DayOfWeek.Monday && week.StartDate.Value.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                week.Warnings.Add("Das Datum liegt auf einem Wochenende – bitte prüfen.");
            if (week.EntryCount == 0) week.Warnings.Add("Es wurden keine Einträge gefunden.");
            return week;
        }
    }

    /// <summary>"2,5", "2.5", "3 h", "3 Std.", "1:30" → hours; null for empty or other text.</summary>
    public static double? ParseHours(string? s)
    {
        var t = (s ?? "").Trim().ToLowerInvariant();
        t = Regex.Replace(t, @"\s*(std\.?|stunden|h)$", "");
        if (t.Length == 0) return null;
        double h;
        var hm = Regex.Match(t, @"^(\d{1,2}):(\d{2})$");
        if (hm.Success) h = int.Parse(hm.Groups[1].Value) + int.Parse(hm.Groups[2].Value) / 60.0;
        else if (!double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out h)) return null;
        return h > 0 && h <= 24 ? Math.Round(h, 2) : null;
    }

    private static string CellText(TableCell c) =>
        string.Join("\n", c.Elements<Paragraph>().Select(DocxTemplateEngine.ParagraphText)).Trim();

    private static DateOnly? ParseDate(string s)
    {
        var m = DateRx.Match(s);
        if (!m.Success) return null;
        var y = int.Parse(m.Groups[3].Value);
        if (y < 100) y += 2000;
        try { return new DateOnly(y, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static void ReadHeader(List<TableRow> rows, ImportWeek week)
    {
        var labels = new (string Key, Regex Rx)[]
        {
            ("nr", new Regex(@"nachweis\s*nr\.?|^nr\.?", RegexOptions.IgnoreCase)),
            ("name", new Regex(@"^name(\s+des\s+auszubildenden)?\s*:?", RegexOptions.IgnoreCase)),
            ("start", new Regex(@"(woche\s+)?vom\s*:?", RegexOptions.IgnoreCase)),
            ("end", new Regex(@"^bis\s*:?", RegexOptions.IgnoreCase)),
            ("dept", new Regex(@"abteilung\s*:?", RegexOptions.IgnoreCase)),
        };
        DateOnly? start = null, end = null;
        foreach (var row in rows.Take(12))
        {
            var cells = row.Elements<TableCell>().ToList();
            for (var i = 0; i < cells.Count; i++)
            {
                var text = CellText(cells[i]);
                if (text.Length == 0) continue;
                foreach (var (key, rx) in labels)
                {
                    var m = rx.Match(text);
                    if (!m.Success) continue;
                    var value = text[(m.Index + m.Length)..].Trim(' ', ':', '\n');
                    if (value.Length == 0 && i + 1 < cells.Count && !labels.Any(l => l.Rx.IsMatch(CellText(cells[i + 1]))))
                        value = CellText(cells[i + 1]);
                    switch (key)
                    {
                        case "nr" when week.ReportNumber.Length == 0: week.ReportNumber = value; break;
                        case "start" when start == null: start = ParseDate(value); break;
                        case "end" when end == null: end = ParseDate(value); break;
                        case "dept" when week.DepartmentLabel.Length == 0: week.DepartmentLabel = value.Replace('\n', ' '); break;
                    }
                    break;
                }
            }
        }
        week.StartDate = start ?? end;
        if (week.StartDate == null) // last resort: the first date anywhere in the header rows
            week.StartDate = rows.Take(6).Select(r => ParseDate(string.Join(" ", r.Elements<TableCell>().Select(CellText)))).FirstOrDefault(d => d != null);
    }

    private static void ReadDays(List<TableRow> rows, ImportWeek week)
    {
        var lines = Enumerable.Range(0, 5).Select(_ => new List<(string Text, string Hours)>()).ToArray();
        var day = -1;
        foreach (var row in rows)
        {
            var cells = row.Elements<TableCell>().ToList();
            if (cells.Count < 2) continue;
            var first = CellText(cells[0]).ToLowerInvariant().TrimEnd(':', '.');
            var idx = Array.FindIndex(WeekdayNames, n => first == n || first.StartsWith(n));
            if (idx >= 0) day = idx;
            else if (first.Length > 0 || row.InnerText.Contains("Unterschrift", StringComparison.OrdinalIgnoreCase)) { if (day >= 0 && first.Length > 0) day = -1; continue; }
            if (day < 0) continue;
            var text = CellText(cells[1]).Replace('\n', ' ').Trim();
            var hours = cells.Count >= 3 ? CellText(cells[^1]) : "";
            if (text.Length > 0 || hours.Length > 0) lines[day].Add((text, hours));
        }

        for (var d = 0; d < 5; d++)
        {
            var entries = new List<ReportEntry>();
            var lastLine = "";
            foreach (var (text, hoursRaw) in lines[d])
            {
                var hours = ParseHours(hoursRaw);
                if (hoursRaw.Length > 0 && hours == null)
                    week.Warnings.Add($"{char.ToUpper(WeekdayNames[d][0])}{WeekdayNames[d][1..]}: Stunden „{hoursRaw}“ nicht erkannt.");
                var firstWord = text.Split(' ', 2)[0];
                var continuation = hours == null && entries.Count > 0 && text.Length > 0 && lastLine.Length > 0
                                   && lastLine.Length + 1 + firstWord.Length > LineChars;
                if (continuation) entries[^1].Text += " " + text;
                else if (text.Length > 0) entries.Add(new ReportEntry { Text = text, Hours = hours });
                else if (hours != null && entries.Count > 0 && entries[^1].Hours == null) entries[^1].Hours = hours;
                lastLine = text;
            }
            week.Days[d].Entries = entries;
            week.Days[d].Type = TypeOf(entries, week.StartDate is { } s ? Generation.Scheduling.WeekBounds(s).Monday.AddDays(d) : null);
        }
    }

    /// <summary>Day type from the lines: single "Urlaub"/"Krank"/… lines, holiday names, Lernfeld topics.</summary>
    private static string TypeOf(List<ReportEntry> entries, DateOnly? date)
    {
        if (entries.Count == 0) return "free";
        var texts = entries.Select(e => e.Text.Trim()).ToList();
        if (texts.Count == 1)
        {
            var t = texts[0].ToLowerInvariant();
            switch (t)
            {
                case "urlaub": return "vacation";
                case "krank" or "arbeitsunfähig": return "sick";
                case "frei": return "free";
                case "feiertag": return "holiday";
                case "berufsschule" or "schule": return "school";
            }
            if (date is { } d && Holidays.AnyStateHolidayName(d) is { } name && name.Equals(texts[0], StringComparison.OrdinalIgnoreCase)) return "holiday";
        }
        if (texts.All(t => SchoolLineRx.IsMatch(t))) return "school";
        return "work";
    }

    private static void ReadRemarks(Body body, ImportWeek week)
    {
        var cell = body.Descendants<TableCell>().LastOrDefault(c => c.Elements<Paragraph>().Any(p => DocxTemplateEngine.ParagraphText(p).Trim() == "Bemerkungen"));
        if (cell == null) return;
        var lines = cell.Elements<Paragraph>().Select(p => DocxTemplateEngine.ParagraphText(p).Trim())
            .Where(t => t.Length > 0 && t != "Bemerkungen" && !t.All(ch => ch == '_')).ToList();
        week.Remarks = string.Join("\n", lines);
    }
}
