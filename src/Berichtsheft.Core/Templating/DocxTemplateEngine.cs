using System.Text;
using System.Text.RegularExpressions;
using Berichtsheft.Core.Signature;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Berichtsheft.Core.Templating;

/// <summary>An image for a placeholder (signatures). Height is fixed, width follows the aspect ratio up to a maximum.</summary>
public sealed class ImageValue
{
    public byte[] Data { get; }
    public double HeightCm { get; }
    public double MaxWidthCm { get; }

    public ImageValue(byte[] data, double heightCm = 1.1, double maxWidthCm = 3.2)
    {
        Data = data; HeightCm = heightCm; MaxWidthCm = maxWidthCm;
    }

    public (double WidthCm, double HeightCm) SizeCm()
    {
        var (pw, ph) = ImageSignature.PixelSize(Data);
        if (pw <= 0 || ph <= 0) return (Math.Min(MaxWidthCm, HeightCm * 3), HeightCm);
        var h = HeightCm;
        var w = h * pw / ph;
        if (w > MaxWidthCm) { w = MaxWidthCm; h = w * ph / pw; }
        return (w, h);
    }
}

public sealed class TemplateInfo
{
    public List<string> Placeholders { get; set; } = new();
    /// <summary>Lines per weekday ("MO" → 6), from the highest MO_n.</summary>
    public Dictionary<string, int> DayCapacity { get; set; } = new();
    public bool HasSignature { get; set; }
    public bool HasEntriesBlock { get; set; }
}

/// <summary>Fills {{PLACEHOLDERS}} in a .docx (body, tables, headers, footers) while keeping the formatting.
/// Word often splits a placeholder over several runs: the runs of a paragraph are concatenated, and only the
/// affected runs are rewritten – the replacement inherits the formatting of the run where the placeholder starts.</summary>
public static class DocxTemplateEngine
{
    public static readonly Regex PlaceholderRx = new(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled);
    public static readonly string[] DayPrefixes = { "MO", "DI", "MI", "DO", "FR", "SA", "SO" };
    private static readonly Regex DayLineRx = new(@"^(MO|DI|MI|DO|FR|SA|SO)_(\d+)$", RegexOptions.Compiled);
    private const long EmuPerCm = 360000;

    // ------------------------------------------------------------ public API
    public static void ValidateDocxFile(string path)
    {
        try
        {
            using var doc = WordprocessingDocument.Open(path, false);
            if (doc.MainDocumentPart?.Document?.Body == null) throw new TemplateError("Die Datei enthält kein Word-Dokument.");
        }
        catch (TemplateError) { throw; }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            throw new TemplateError("Die Datei ist kein gültiges Word-Dokument (.docx).", exc);
        }
    }

    public static TemplateInfo Inspect(string path)
    {
        ValidateDocxFile(path);
        using var doc = WordprocessingDocument.Open(path, false);
        var names = new List<string>();
        foreach (var (root, _) in Roots(doc))
            foreach (var p in root.Descendants<Paragraph>())
                foreach (Match m in PlaceholderRx.Matches(ParagraphText(p)))
                {
                    var n = m.Groups[1].Value.ToUpperInvariant();
                    if (!names.Contains(n)) names.Add(n);
                }
        return InfoFrom(names);
    }

    /// <summary>Capacity and flags from a list of placeholder names (also used for stored templates).</summary>
    public static TemplateInfo InfoFrom(IEnumerable<string> placeholders)
    {
        var info = new TemplateInfo { Placeholders = placeholders.ToList() };
        foreach (var n in info.Placeholders)
        {
            var m = DayLineRx.Match(n);
            if (m.Success)
            {
                var line = int.Parse(m.Groups[2].Value);
                info.DayCapacity[m.Groups[1].Value] = Math.Max(info.DayCapacity.GetValueOrDefault(m.Groups[1].Value), line);
            }
        }
        info.HasSignature = info.Placeholders.Contains("SIGNATURE");
        info.HasEntriesBlock = info.Placeholders.Contains("ENTRIES");
        return info;
    }

    /// <summary>Renders the template. Values: string, <see cref="ImageValue"/> or a list of strings (paragraph
    /// repeated per item). Placeholders without a value are removed.</summary>
    public static byte[] Render(string templatePath, Dictionary<string, object> values)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(templatePath); }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            throw new TemplateError("Die Vorlagendatei konnte nicht gelesen werden.", exc);
        }
        return Render(bytes, values);
    }

    public static byte[] Render(byte[] template, Dictionary<string, object> values)
    {
        var ms = new MemoryStream();
        ms.Write(template, 0, template.Length);
        WordprocessingDocument doc;
        try { doc = WordprocessingDocument.Open(ms, true); }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            throw new TemplateError("Die Vorlage ist kein gültiges Word-Dokument (.docx).", exc);
        }
        using (doc)
        {
            if (doc.MainDocumentPart?.Document?.Body == null) throw new TemplateError("Die Vorlage enthält kein Word-Dokument.");
            var ctx = new RenderState(values);
            foreach (var (root, part) in Roots(doc))
            {
                foreach (var p in root.Descendants<Paragraph>().ToList()) ProcessParagraph(p, part, ctx);
                root.Save();
            }
        }
        return ms.ToArray();
    }

    /// <summary>Makes text safe for XML: Word line breaks (\v) and CR/LF become \n, other control characters and
    /// lone surrogates are removed.</summary>
    public static string CleanText(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u000B', '\n');
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; }
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;
            if (c < 0x20 && c != '\t' && c != '\n') continue;
            if (c is '￾' or '￿') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ internals
    private sealed class RenderState
    {
        public Dictionary<string, object> Values { get; }
        public uint NextDrawingId = 1000;
        public RenderState(Dictionary<string, object> values) =>
            Values = new Dictionary<string, object>(values, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<(OpenXmlPartRootElement Root, OpenXmlPart Part)> Roots(WordprocessingDocument doc)
    {
        var main = doc.MainDocumentPart!;
        yield return (main.Document, main);
        foreach (var h in main.HeaderParts) if (h.Header != null) yield return (h.Header, h);
        foreach (var f in main.FooterParts) if (f.Footer != null) yield return (f.Footer, f);
    }

    /// <summary>The text elements that belong to this paragraph (not to paragraphs nested in text boxes).</summary>
    private static List<Text> TextsOf(Paragraph p) =>
        p.Descendants<Text>().Where(t => t.Ancestors<Paragraph>().FirstOrDefault() == p).ToList();

    public static string ParagraphText(Paragraph p) => string.Concat(TextsOf(p).Select(t => t.Text));

    private static void ProcessParagraph(Paragraph p, OpenXmlPart part, RenderState ctx, string? listName = null, string? listItem = null)
    {
        if (p.Parent == null) return;
        var texts = TextsOf(p);
        var full = string.Concat(texts.Select(t => t.Text));
        if (!full.Contains("{{")) return;
        var matches = PlaceholderRx.Matches(full).Cast<Match>().ToList();
        if (matches.Count == 0) return;

        // list value: one copy of the paragraph per item
        if (listName == null)
        {
            var list = matches.Select(m => m.Groups[1].Value.ToUpperInvariant())
                .FirstOrDefault(n => ctx.Values.TryGetValue(n, out var v) && v is IEnumerable<string> && v is not string);
            if (list != null)
            {
                foreach (var item in (IEnumerable<string>)ctx.Values[list])
                {
                    var clone = (Paragraph)p.CloneNode(true);
                    p.InsertBeforeSelf(clone);
                    ProcessParagraph(clone, part, ctx, list, item);
                }
                p.Remove();
                return;
            }
        }

        // positions: char index in the concatenated text → (text element, offset)
        var starts = new int[texts.Count];
        for (int i = 0, pos = 0; i < texts.Count; i++) { starts[i] = pos; pos += texts[i].Text.Length; }
        int ElementAt(int charIndex)
        {
            for (var i = texts.Count - 1; i >= 0; i--) if (starts[i] <= charIndex && texts[i].Text.Length > 0) return i;
            return 0;
        }

        var images = new List<(Text At, ImageValue Image)>();
        for (var k = matches.Count - 1; k >= 0; k--)
        {
            var m = matches[k];
            var name = m.Groups[1].Value.ToUpperInvariant();
            object? value = name == listName ? listItem : ctx.Values.GetValueOrDefault(name);
            var repl = value switch
            {
                string s => CleanText(s),
                ImageValue => "",
                null => "",
                IEnumerable<string> l => CleanText(string.Join("\n", l)),
                _ => CleanText(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)),
            };
            var si = ElementAt(m.Index);
            var ei = ElementAt(m.Index + m.Length - 1);
            var sOff = m.Index - starts[si];
            var eOff = m.Index + m.Length - starts[ei];
            var st = texts[si];
            if (si == ei) st.Text = st.Text[..sOff] + repl + st.Text[eOff..];
            else
            {
                st.Text = st.Text[..sOff] + repl;
                for (var i = si + 1; i < ei; i++) texts[i].Text = "";
                texts[ei].Text = texts[ei].Text[eOff..];
                texts[ei].Space = SpaceProcessingModeValues.Preserve;
            }
            st.Space = SpaceProcessingModeValues.Preserve;
            if (value is ImageValue img) images.Add((st, img));
        }

        foreach (var (at, img) in images)
        {
            var run = at.Ancestors<Run>().FirstOrDefault();
            var drawingRun = new Run(ImageDrawing(part, img, ctx));
            if (run != null) run.InsertAfterSelf(drawingRun); else p.AppendChild(drawingRun);
        }
        foreach (var t in texts.Where(t => t.Text.Contains('\n')).ToList()) SplitLines(t);
    }

    /// <summary>"a\nb" inside one text element → a, line break, b (same run, same formatting).</summary>
    private static void SplitLines(Text t)
    {
        var parts = t.Text.Split('\n');
        t.Text = parts[0];
        OpenXmlElement last = t;
        for (var i = 1; i < parts.Length; i++)
        {
            var br = new Break();
            last.InsertAfterSelf(br);
            var nt = new Text(parts[i]) { Space = SpaceProcessingModeValues.Preserve };
            br.InsertAfterSelf(nt);
            last = nt;
        }
    }

    private static Drawing ImageDrawing(OpenXmlPart part, ImageValue img, RenderState ctx)
    {
        ImagePart imagePart = part switch
        {
            MainDocumentPart m => m.AddImagePart(ImagePartType.Png),
            HeaderPart h => h.AddImagePart(ImagePartType.Png),
            FooterPart f => f.AddImagePart(ImagePartType.Png),
            _ => throw new TemplateError("Bilder können an dieser Stelle der Vorlage nicht eingefügt werden."),
        };
        using (var s = new MemoryStream(img.Data)) imagePart.FeedData(s);
        var relId = part.GetIdOfPart(imagePart);
        var (wCm, hCm) = img.SizeCm();
        return BuildDrawing(relId, (long)(wCm * EmuPerCm), (long)(hCm * EmuPerCm), ctx.NextDrawingId++);
    }

    /// <summary>Inline picture element for an image part relation.</summary>
    public static Drawing BuildDrawing(string relId, long cx, long cy, uint id)
    {
        return new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = cx, Cy = cy },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = id, Name = "Unterschrift " + id },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = 0U, Name = $"signature{id}.png" },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(new A.Blip { Embed = relId }, new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = cx, Cy = cy }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }))
                    ) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })
            ) { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });
    }
}
