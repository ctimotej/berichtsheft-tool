using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Berichtsheft.Core.Templating;

/// <summary>Inserts the {{PLACEHOLDERS}} into the original form "Tagesbericht Gewerblich-Technische Berufe"
/// (converted to .docx). Cells are addressed by their <b>grid column</b>, so merged cells do not shift the map.
/// Layout of the form: a header table (Nr., Name, Woche vom/bis, Abteilung, Ausbildungsjahr) and a body table with
/// a header row, 5 × 6 day lines (text in grid column 1, hours in grid column 5) and the signature row.</summary>
public static class TemplatePreparer
{
    private static readonly (int Row, int Col, string Name)[] HeaderMap =
    {
        (0, 3, "NR"), (0, 10, "NAME"), (2, 2, "START_DATE"), (2, 5, "END_DATE"), (2, 12, "DEPARTMENT"), (3, 1, "TRAINING_YEAR"),
    };
    private static readonly string[] Days = { "MO", "DI", "MI", "DO", "FR" };
    private const int LinesPerDay = 6, TextCol = 1, HoursCol = 5;

    public static void Prepare(string sourcePath, string targetPath)
    {
        DocxTemplateEngine.ValidateDocxFile(sourcePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Copy(sourcePath, targetPath, overwrite: true);
        try
        {
            using var doc = WordprocessingDocument.Open(targetPath, true);
            var body = doc.MainDocumentPart!.Document.Body!;
            if (body.Descendants<Paragraph>().Any(p => DocxTemplateEngine.PlaceholderRx.IsMatch(DocxTemplateEngine.ParagraphText(p))))
                throw new TemplateError("Die Datei enthält bereits {{PLATZHALTER}} – sie ist schon vorbereitet.");
            var tables = body.Elements<Table>().ToList();
            var header = tables.FirstOrDefault() ?? throw new TemplateError("Im Dokument wurde keine Tabelle gefunden – es ist nicht das erwartete Formular.");
            var main = tables.Skip(1).FirstOrDefault(t => t.Elements<TableRow>().Count() >= 1 + Days.Length * LinesPerDay)
                       ?? throw new TemplateError("Die Tabelle mit den Wochentagen (5 × 6 Zeilen) wurde nicht gefunden.");

            var hRows = header.Elements<TableRow>().ToList();
            foreach (var (row, col, name) in HeaderMap)
                if (row < hRows.Count && CellAt(hRows[row], col) is { } c) Put(c, 0, name);

            var rows = main.Elements<TableRow>().ToList();
            for (var d = 0; d < Days.Length; d++)
                for (var line = 1; line <= LinesPerDay; line++)
                {
                    var r = rows[1 + d * LinesPerDay + line - 1];
                    if (CellAt(r, TextCol) is { } t) Put(t, 0, $"{Days[d]}_{line}");
                    if (CellAt(r, HoursCol) is { } h) Put(h, 0, $"{Days[d]}_{line}_H", fontHalfPoints: 18);
                }

            var sig = rows.Count > 1 + Days.Length * LinesPerDay + 1 ? rows[1 + Days.Length * LinesPerDay + 1] : rows.Last();
            if (CellAt(sig, 0) is { } trainee) { Put(trainee, 0, "SIGNATURE"); Put(trainee, 1, "SIGNATURE_DATE"); }
            if (CellAt(sig, 2) is { } trainer) { Put(trainer, 0, "TRAINER_SIGNATURE"); Put(trainer, 1, "TRAINER_SIGNATURE_DATE"); }
            if (CellAt(sig, 4) is { } remarks) Put(remarks, 0, "REMARKS");
            doc.MainDocumentPart.Document.Save();
        }
        catch
        {
            try { File.Delete(targetPath); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>The cell of a row that covers grid column <paramref name="gridCol"/>.</summary>
    public static TableCell? CellAt(TableRow row, int gridCol)
    {
        var col = row.TableRowProperties?.GetFirstChild<GridBefore>()?.Val?.Value ?? 0;
        foreach (var c in row.Elements<TableCell>())
        {
            var span = c.TableCellProperties?.GridSpan?.Val?.Value ?? 1;
            if (gridCol >= col && gridCol < col + span) return c;
            col += span;
        }
        return null;
    }

    /// <summary>Writes "{{name}}" into paragraph <paramref name="index"/> of the cell (added if missing), using the
    /// paragraph's own run formatting.</summary>
    private static void Put(TableCell cell, int index, string name, int? fontHalfPoints = null)
    {
        var paras = cell.Elements<Paragraph>().ToList();
        while (paras.Count <= index)
        {
            var np = new Paragraph();
            if (paras.Count > 0) paras[^1].InsertAfterSelf(np); else cell.AppendChild(np);
            paras.Add(np);
        }
        var p = paras[index];
        var text = $"{{{{{name}}}}}";
        var existing = DocxTemplateEngine.ParagraphText(p);
        RunProperties? rp = p.Elements<Run>().FirstOrDefault()?.RunProperties?.CloneNode(true) as RunProperties;
        if (rp == null && p.ParagraphProperties?.ParagraphMarkRunProperties is { } mark)
            rp = new RunProperties(mark.ChildElements.Select(e => e.CloneNode(true)));
        rp ??= new RunProperties();
        if (fontHalfPoints is { } fs)
        {
            rp.FontSize = new FontSize { Val = fs.ToString() };
            rp.FontSizeComplexScript = new FontSizeComplexScript { Val = fs.ToString() };
        }
        var run = new Run(rp, new Text(existing.Trim().Length > 0 ? " " + text : text) { Space = SpaceProcessingModeValues.Preserve });
        p.AppendChild(run);
    }
}
