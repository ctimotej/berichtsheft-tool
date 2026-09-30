using System.IO.Compression;
using Berichtsheft.Core.Data;
using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Storage;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;

namespace Berichtsheft.Core.Services;

/// <summary>Complete export for the final exam: all Ausbildungsnachweise of a period in one PDF (or
/// a ZIP of Word files), with a cover sheet listing every week, missing weeks included. Optionally
/// signs every report that the trainee has not signed yet (default signature).
/// Ported from app/services/exam_service.py (PDF merge: PDFsharp instead of pypdf).</summary>
public sealed class ExamService
{
    private static readonly HashSet<string> Editable = new() { "draft", "exported", "signed" };
    private static readonly double[] ColWidthsCm = { 1.3, 2.0, 4.4, 5.0, 1.8, 2.7 }; // Nr, KW, Zeitraum, Abteilung, Azubi, Ausbilder

    private readonly ReportService _r;
    private readonly BerichtsheftDbContext _db;

    public ExamService(ReportService reports) { _r = reports; _db = reports.Db; }

    private (DateOnly? Start, DateOnly? End) Range(ExamExportRequest req)
    {
        var cfg = _r.SettingsSvc.Get();
        return (req.StartDate ?? cfg.TrainingStartDate, req.EndDate ?? cfg.TrainingEndDate ?? DateOnly.FromDateTime(DateTime.Today));
    }

    private List<ReportEntity> Reports(DateOnly? start, DateOnly? end)
    {
        var q = _db.Reports.Include(r => r.Signatures).AsEnumerable();
        if (start != null) q = q.Where(r => r.EndDate >= start);
        if (end != null) q = q.Where(r => r.StartDate <= end);
        return q.OrderBy(r => r.StartDate).ToList();
    }

    private static bool Signed(ReportEntity r) => r.Signatures.Any(s => s.Role == "trainee");

    private List<CoverageWeek> Missing(DateOnly? start, DateOnly? end) =>
        _r.Coverage().Weeks.Where(w => w.Status == "missing" && (start == null || w.EndDate >= start) && (end == null || w.StartDate <= end)).ToList();

    public ExamPreview Preview(ExamExportRequest req)
    {
        var (start, end) = Range(req);
        var reps = Reports(start, end);
        var unsigned = reps.Where(r => !Signed(r)).ToList();
        return new ExamPreview
        {
            StartDate = start, EndDate = end, Reports = reps.Count, Unsigned = unsigned.Count,
            AutoSignable = unsigned.Count(r => Editable.Contains(r.Status)),
            MissingWeeks = Missing(start, end).Count, PdfAvailable = _r.Ctx.Converter.PdfAvailable,
            HasDefaultSignature = _r.SettingsSvc.GetDefaultSignature() != null,
        };
    }

    public (byte[] Data, string Name, string Mime) Export(ExamExportRequest req)
    {
        var (start, end) = Range(req);
        var reps = Reports(start, end);
        if (reps.Count == 0) throw new InvalidInputError("Im gewählten Zeitraum gibt es keine Berichte.");
        if (req.Format == "pdf" && !_r.Ctx.Converter.PdfAvailable)
            throw new InvalidInputError("Für den PDF-Export wird Microsoft Word oder LibreOffice benötigt – bitte ZIP wählen.");
        var todo = reps.Where(r => !Signed(r) && Editable.Contains(r.Status)).ToList();
        if (req.AutoSign && todo.Count > 0)
        {
            if (_r.SettingsSvc.GetDefaultSignature() == null)
                throw new InvalidInputError("Zum automatischen Unterschreiben bitte zuerst eine Standard-Unterschrift speichern.");
            foreach (var r in todo) _r.Sign(r.Id, new SignRequest { UseDefault = true });
        }
        var docs = new List<(ReportEntity Report, byte[] Data, string Name)>();
        foreach (var r in reps)
        {
            _db.Entry(r).Reload();
            _db.Entry(r).Collection(x => x.Signatures).Load();
            if (string.IsNullOrEmpty(r.DocumentKey) || r.Status == "draft") _r.Export(r.Id);
            var (data, name) = _r.Download(r.Id);
            docs.Add((r, data, name));
        }
        var cfg = _r.SettingsSvc.Get();
        var cover = req.IncludeCover ? Cover(docs, Missing(start, end), start, end) : null;
        var who = (cfg.TraineeName.Length > 0 ? cfg.TraineeName : "Azubi").Replace(" ", "_");
        var b = Filenames.SafeFilename(start != null && end != null
            ? $"Ausbildungsnachweise_{who}_{start:yyyy-MM-dd}_{end:yyyy-MM-dd}" : "Ausbildungsnachweise");
        return req.Format == "zip"
            ? (Zip(cover, docs), $"{b}.zip", "application/zip")
            : (Pdf(cover, docs), $"{b}.pdf", "application/pdf");
    }

    private static byte[] Zip(byte[]? cover, List<(ReportEntity Report, byte[] Data, string Name)> docs)
    {
        using var buf = new MemoryStream();
        using (var z = new ZipArchive(buf, ZipArchiveMode.Create, true))
        {
            void Add(string n, byte[] d) { using var s = z.CreateEntry(n, CompressionLevel.Optimal).Open(); s.Write(d); }
            if (cover != null) Add("000_Deckblatt_und_Uebersicht.docx", cover);
            for (var i = 0; i < docs.Count; i++) Add($"{i + 1:000}_{docs[i].Name}", docs[i].Data);
        }
        return buf.ToArray();
    }

    private byte[] Pdf(byte[]? cover, List<(ReportEntity Report, byte[] Data, string Name)> docs)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "bh-exam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var files = new List<string>();
            if (cover != null)
            {
                var c = Path.Combine(tmpDir, "000_cover.docx");
                File.WriteAllBytes(c, cover);
                files.Add(c);
            }
            for (var i = 0; i < docs.Count; i++)
            {
                var p = Path.Combine(tmpDir, $"{i + 1:000}.docx");
                File.WriteAllBytes(p, docs[i].Data);
                files.Add(p);
            }
            var pdfs = _r.Ctx.Converter.ConvertManyToPdf(files, tmpDir, 60 + 5 * files.Count);
            return PdfMerge.Merge(pdfs);
        }
        finally { try { Directory.Delete(tmpDir, true); } catch { /* best effort */ } }
    }

    // ---------------------------------------------------------------- cover sheet (DOCX built with OpenXml)
    private static RunProperties Rp(bool bold = false, int halfPoints = 20)
    {
        var rp = new RunProperties(new RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial" });
        if (bold) rp.AppendChild(new Bold());
        rp.AppendChild(new FontSize { Val = halfPoints.ToString() });
        return rp;
    }

    private static Paragraph Para(string text, bool bold = false, int halfPoints = 20, JustificationValues? align = null)
    {
        var p = new Paragraph();
        if (align != null) p.AppendChild(new ParagraphProperties(new Justification { Val = align }));
        p.AppendChild(new Run(Rp(bold, halfPoints), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        return p;
    }

    private static TableCell Cell(string text, bool bold, double? widthCm)
    {
        var props = new TableCellProperties();
        if (widthCm != null) props.AppendChild(new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = ((int)(widthCm.Value * 567)).ToString() });
        return new TableCell(props, Para(text, bold));
    }

    private static BorderType Border<T>() where T : BorderType, new() => new T { Val = BorderValues.Single, Size = 4, Color = "000000" };

    private byte[] Cover(List<(ReportEntity Report, byte[] Data, string Name)> docs, List<CoverageWeek> missing, DateOnly? start, DateOnly? end)
    {
        var cfg = _r.SettingsSvc.Get();
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, true))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);
            body.AppendChild(Para("Ausbildungsnachweise", true, 40, JustificationValues.Center));

            var info = new Table(new TableProperties(new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }));
            string Dash(string? v) => string.IsNullOrEmpty(v) ? "–" : v;
            foreach (var (k, v) in new[]
            {
                ("Name", cfg.TraineeName), ("Ausbildungsberuf", cfg.Profession), ("Ausbildungsbetrieb", cfg.Company),
                ("Ausbildungsbeginn", cfg.TrainingStartDate is { } ts ? ts.ToString("dd.MM.yyyy") : ""),
                ("Zeitraum", start != null && end != null ? $"{start:dd.MM.yyyy} – {end:dd.MM.yyyy}" : ""),
                ("Anzahl Nachweise", docs.Count.ToString()), ("Erstellt am", DateTime.Now.ToString("dd.MM.yyyy")),
            })
                info.AppendChild(new TableRow(Cell(k, true, null), Cell(Dash(v), false, null)));
            body.AppendChild(info);

            body.AppendChild(new Paragraph());
            body.AppendChild(Para("Übersicht", true));
            var t = new Table(new TableProperties(
                new TableBorders(Border<TopBorder>(), Border<LeftBorder>(), Border<BottomBorder>(), Border<RightBorder>(),
                                 Border<InsideHorizontalBorder>(), Border<InsideVerticalBorder>()),
                new TableLayout { Type = TableLayoutValues.Fixed }));
            var grid = new TableGrid();
            foreach (var w in ColWidthsCm) grid.AppendChild(new GridColumn { Width = ((int)(w * 567)).ToString() });
            t.AppendChild(grid);
            var header = new TableRow();
            var heads = new[] { "Nr.", "KW", "Zeitraum", "Abteilung", "Azubi", "Ausbilder" };
            for (var i = 0; i < heads.Length; i++) header.AppendChild(Cell(heads[i], true, ColWidthsCm[i]));
            t.AppendChild(header);
            foreach (var (r, _, _) in docs)
            {
                var trainer = r.Signatures.Any(s => s.Role == "trainer");
                var vals = new[]
                {
                    r.ReportNumber ?? "", $"{r.CalendarWeek}/{r.Year}", $"{r.StartDate:dd.MM.} – {r.EndDate:dd.MM.yyyy}",
                    r.DepartmentLabel ?? "", Signed(r) ? "✓" : "offen", trainer ? "✓" : "offen",
                };
                var row = new TableRow();
                for (var i = 0; i < vals.Length; i++) row.AppendChild(Cell(vals[i], false, ColWidthsCm[i]));
                t.AppendChild(row);
            }
            body.AppendChild(t);
            if (missing.Count > 0)
            {
                body.AppendChild(new Paragraph());
                body.AppendChild(Para($"Fehlende Wochen ({missing.Count})", true));
                body.AppendChild(Para(string.Join(", ", missing.Select(w => $"KW {w.CalendarWeek}/{w.Year}"))));
            }
            main.Document.Save();
        }
        return ms.ToArray();
    }
}
