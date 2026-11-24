using Berichtsheft.Core.Signature;
using Berichtsheft.Core.Templating;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;

namespace Berichtsheft.Core.Services;

/// <summary>Puts the Ausbilder's signature and the date into the field "Ausbildender bzw. Ausbilder" of a finished
/// Berichtsheft – in a Word file or in a PDF. Already signed files are refused.</summary>
public static class TrainerSigner
{
    private const string PdfMarker = "/BerichtsheftTrainerSigned";

    // ------------------------------------------------------------ Word
    public static byte[] SignDocx(byte[] docx, byte[] signaturePng, DateOnly date)
    {
        var img = CheckImage(signaturePng);
        var ms = new MemoryStream();
        ms.Write(docx);
        WordprocessingDocument doc;
        try { doc = WordprocessingDocument.Open(ms, true); }
        catch (Exception exc) when (exc is not OutOfMemoryException) { throw new TemplateError("Die Word-Datei kann nicht gelesen werden (beschädigt oder geschützt).", exc); }
        using (doc)
        {
            var main = doc.MainDocumentPart ?? throw new TemplateError("Die Datei enthält kein Word-Dokument.");
            var body = main.Document?.Body ?? throw new TemplateError("Die Datei enthält kein Word-Dokument.");
            var cell = body.Descendants<TableCell>().LastOrDefault(IsTrainerCell)
                       ?? throw new TemplateError("Im Dokument gibt es kein Feld „Ausbildender bzw. Ausbilder“ – es ist nicht das erwartete Formular.");
            if (cell.Descendants<Drawing>().Any()) throw new TemplateError("Das Dokument ist bereits vom Ausbilder unterschrieben.");

            var paras = cell.Elements<Paragraph>().ToList();
            var u = paras.FindIndex(p => DocxTemplateEngine.ParagraphText(p).Trim() is { Length: >= 3 } t && t.All(c => c == '_'));
            if (u < 0) u = Math.Max(0, paras.FindIndex(p => DocxTemplateEngine.ParagraphText(p).Contains("Ausbilder")));
            var anchor = paras[u];
            Paragraph imgP, dateP;
            if (u >= 2 && Empty(paras[u - 2]) && Empty(paras[u - 1])) { imgP = paras[u - 2]; dateP = paras[u - 1]; }
            else
            {
                imgP = NewParagraphLike(anchor); dateP = NewParagraphLike(anchor);
                anchor.InsertBeforeSelf(imgP);
                anchor.InsertBeforeSelf(dateP);
            }
            foreach (var t in imgP.Descendants<Text>().Concat(dateP.Descendants<Text>())) t.Text = "";

            var part = main.AddImagePart(ImagePartType.Png);
            using (var s = new MemoryStream(signaturePng)) part.FeedData(s);
            var (wCm, hCm) = img.SizeCm();
            var maxId = body.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>().Select(d => d.Id?.Value ?? 0).DefaultIfEmpty(0U).Max();
            imgP.AppendChild(new Run(DocxTemplateEngine.BuildDrawing(main.GetIdOfPart(part), (long)(wCm * 360000), (long)(hCm * 360000), maxId + 1)));
            var rp = anchor.Elements<Run>().FirstOrDefault()?.RunProperties?.CloneNode(true);
            var dateRun = new Run(new Text(date.ToString("dd.MM.yyyy")) { Space = SpaceProcessingModeValues.Preserve });
            if (rp != null) dateRun.PrependChild(rp);
            dateP.AppendChild(dateRun);
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static bool IsTrainerCell(TableCell c)
    {
        var t = c.InnerText;
        return t.Contains("Ausbilder") && t.Contains("Unterschrift") && !t.Contains("Auszubildende") && !c.Descendants<Table>().Any();
    }

    private static bool Empty(Paragraph p) => DocxTemplateEngine.ParagraphText(p).Trim().Length == 0 && !p.Descendants<Drawing>().Any();

    private static Paragraph NewParagraphLike(Paragraph p)
    {
        var np = new Paragraph();
        if (p.ParagraphProperties?.CloneNode(true) is ParagraphProperties pp) np.AppendChild(pp);
        return np;
    }

    private static ImageValue CheckImage(byte[] png)
    {
        var (w, h) = ImageSignature.PixelSize(png);
        if (w <= 0 || h <= 0) throw new SignatureError("Die gespeicherte Unterschrift ist kein lesbares Bild – bitte neu festlegen.");
        return new ImageValue(png, 1.0, 3.2);
    }

    // ------------------------------------------------------------ PDF
    public static byte[] SignPdf(byte[] pdf, byte[] signaturePng, DateOnly date)
    {
        var img = CheckImage(signaturePng);
        var targets = new List<(int Page, double Left, double Bottom)>(); // PDF coordinates (origin bottom left)
        try
        {
            using var reader = UglyToad.PdfPig.PdfDocument.Open(pdf);
            for (var i = 1; i <= reader.NumberOfPages; i++)
            {
                var page = reader.GetPage(i);
                var words = page.GetWords().ToList();
                foreach (var label in words.Where(w => w.Text.StartsWith("Ausbildender")))
                {
                    var lb = label.BoundingBox;
                    var line = words.Where(w => w.Text.Length >= 3 && w.Text.All(c => c == '_')
                                                && w.BoundingBox.Bottom >= lb.Top - 2 && w.BoundingBox.Bottom - lb.Top < 30
                                                && w.BoundingBox.Right > lb.Left && w.BoundingBox.Left < lb.Right)
                        .OrderBy(w => w.BoundingBox.Bottom).FirstOrDefault();
                    targets.Add((i, Math.Min(lb.Left, line?.BoundingBox.Left ?? lb.Left), (line?.BoundingBox.Top ?? lb.Top) + 2));
                }
            }
        }
        catch (Exception exc) when (exc is not OutOfMemoryException and not AppError)
        {
            throw new TemplateError("Die PDF-Datei kann nicht gelesen werden (beschädigt, geschützt oder kein PDF).", exc);
        }
        if (targets.Count == 0)
            throw new TemplateError("In der PDF-Datei gibt es kein Feld „Ausbildender bzw. Ausbilder“ (oder sie ist ein Scan ohne Text).");

        PdfSharp.Pdf.PdfDocument doc;
        try { doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify); }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            throw new TemplateError("Die PDF-Datei kann nicht geändert werden (geschützt oder beschädigt).", exc);
        }
        using (doc)
        {
            if (doc.Info.Elements.ContainsKey(PdfMarker)) throw new TemplateError("Das Dokument ist bereits vom Ausbilder unterschrieben.");
            var (wCm, hCm) = img.SizeCm();
            double w = wCm / 2.54 * 72, h = hCm / 2.54 * 72;
            var font = PdfFonts.TryFont(8);
            foreach (var (pageNo, left, bottom) in targets)
            {
                var page = doc.Pages[pageNo - 1];
                var ph = page.Height.Point;
                using var g = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                using (var xi = XImage.FromStream(new MemoryStream(signaturePng, 0, signaturePng.Length, false, true)))
                    g.DrawImage(xi, left, ph - bottom - h, w, h);
                if (font != null) g.DrawString(date.ToString("dd.MM.yyyy"), font, XBrushes.Black, left + w + 4, ph - bottom - 2);
            }
            doc.Info.Elements[PdfMarker] = new PdfString(date.ToString("yyyy-MM-dd"));
            using var outMs = new MemoryStream();
            doc.Save(outMs, false);
            return outMs.ToArray();
        }
    }
}

/// <summary>Font for text drawn into PDFs (PDFsharp needs font files). Uses Arial on Windows, DejaVu/Liberation on Linux;
/// null when none is found – the date is then left out of the PDF, the signature image is still placed.</summary>
internal static class PdfFonts
{
    private static readonly object Lock = new();
    private static bool _setup;
    private static byte[]? _font;

    public static XFont? TryFont(double size)
    {
        lock (Lock)
        {
            if (!_setup)
            {
                _setup = true;
                var candidates = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"),
                    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
                    "/usr/share/fonts/dejavu/DejaVuSans.ttf", "/System/Library/Fonts/Supplemental/Arial.ttf", "/Library/Fonts/Arial.ttf",
                };
                var file = candidates.FirstOrDefault(f => f.Length > 0 && File.Exists(f));
                if (file != null)
                {
                    try
                    {
                        _font = File.ReadAllBytes(file);
                        if (GlobalFontSettings.FontResolver == null) GlobalFontSettings.FontResolver = new Resolver();
                    }
                    catch (Exception) { _font = null; }
                }
            }
            if (_font == null) return null;
            try { return new XFont("BH Sans", size); }
            catch (Exception) { return null; }
        }
    }

    private sealed class Resolver : IFontResolver
    {
        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) => new("BHSans");
        public byte[]? GetFont(string faceName) => _font;
    }
}
