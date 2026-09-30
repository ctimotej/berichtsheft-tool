using System.IO.Compression;
using Berichtsheft.Core;
using Berichtsheft.Core.Templating;
using Berichtsheft.Tests.TestHelpers;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Berichtsheft.Tests.Templating;

// Ported from tests/test_docx_engine.py
public class DocxTemplateEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bh-docx-" + Guid.NewGuid().ToString("N"));

    public DocxTemplateEngineTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private string NewDoc(Action<Body, MainDocumentPart> build, Action<MainDocumentPart>? headers = null)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".docx");
        using var doc = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        build(main.Document.Body!, main);
        headers?.Invoke(main);
        main.Document.Save();
        return path;
    }

    private static Paragraph P(params string[] runTexts) =>
        new(runTexts.Select(t => (DocumentFormat.OpenXml.OpenXmlElement)new Run(new Text(t) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve })));

    private static List<string> Paragraphs(byte[] data)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(data), false);
        return doc.MainDocumentPart!.Document.Body!.Descendants<Paragraph>()
            .Select(p => string.Concat(p.Descendants<Text>().Select(t => t.Text))).ToList();
    }

    [Fact]
    public void ReplacesPlaceholderSplitOverRunsAndKeepsFormatting()
    {
        var path = NewDoc((body, _) =>
        {
            var bold = new Run(new RunProperties(new Bold()), new Text("{{NA") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve });
            body.Append(new Paragraph(
                new Run(new Text("Name: ") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }),
                bold,
                new Run(new Text("ME}} Ende") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve })));
        });
        var output = DocxTemplateEngine.Render(path, new() { ["NAME"] = "Max Muster" });
        Assert.Equal("Name: Max Muster Ende", Paragraphs(output)[0]);
        using var doc = WordprocessingDocument.Open(new MemoryStream(output), false);
        var boldRun = doc.MainDocumentPart!.Document.Body!.Descendants<Run>()
            .First(r => r.RunProperties?.Bold != null);
        Assert.Contains("Max", string.Concat(boldRun.Descendants<Text>().Select(t => t.Text)));
    }

    [Fact]
    public void ReplacesInTablesAndHeaders()
    {
        var path = NewDoc((body, _) =>
        {
            body.Append(new Table(new TableRow(
                new TableCell(P("{{MO_1}}")), new TableCell(P("{{MO_1_H}}")))));
        }, main =>
        {
            var hp = main.AddNewPart<HeaderPart>();
            hp.Header = new Header(P("KW {{KW}}"));
            hp.Header.Save();
            main.Document.Body!.Append(new SectionProperties(
                new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(hp) }));
        });
        var output = DocxTemplateEngine.Render(path, new() { ["KW"] = "40", ["MO_1"] = "Support", ["MO_1_H"] = "3" });
        using var doc = WordprocessingDocument.Open(new MemoryStream(output), false);
        var all = string.Concat(doc.MainDocumentPart!.Document.Descendants<Text>().Select(t => t.Text)) +
                  string.Concat(doc.MainDocumentPart.HeaderParts.SelectMany(h => h.Header.Descendants<Text>()).Select(t => t.Text));
        Assert.Contains("KW 40", all);
        Assert.Contains("Support", all);
        Assert.DoesNotContain("{{", all);
    }

    [Fact]
    public void UnknownPlaceholdersAreRemoved()
    {
        var path = NewDoc((body, _) => body.Append(P("A {{UNBEKANNT}} B")));
        Assert.Equal("A  B", Paragraphs(DocxTemplateEngine.Render(path, new()))[0]);
    }

    [Fact]
    public void ListPlaceholderExpandsToParagraphs()
    {
        var path = NewDoc((body, _) => { body.Append(P("{{ENTRIES}}")); body.Append(P("Ende")); });
        var output = DocxTemplateEngine.Render(path, new() { ["ENTRIES"] = new List<string> { "Eins", "Zwei", "Drei" } });
        Assert.Equal(new[] { "Eins", "Zwei", "Drei", "Ende" }, Paragraphs(output));
    }

    [Fact]
    public void ImagePlaceholderEmbedsPicture()
    {
        var path = NewDoc((body, _) => body.Append(P("Unterschrift: {{SIGNATURE}}")));
        var output = DocxTemplateEngine.Render(path, new() { ["SIGNATURE"] = new ImageValue(SignatureFixtures.MakeSignaturePng()) });
        using var zip = new ZipArchive(new MemoryStream(output));
        Assert.Contains(zip.Entries, e => e.FullName.Contains("media/")); // location differs from python-docx (word/media); both valid OPC
        Assert.DoesNotContain("{{", Paragraphs(output)[0]);
        // the result must still be a readable document
        using var doc = WordprocessingDocument.Open(new MemoryStream(output), false);
        Assert.NotEmpty(doc.MainDocumentPart!.Document.Descendants<Drawing>());
    }

    [Fact]
    public void ImageIsScaledToMaxWidth()
    {
        var (w, h) = new ImageValue(SignatureFixtures.MakeSignaturePng(), heightCm: 2, maxWidthCm: 3).SizeCm();
        Assert.True(w <= 3.0001 && h <= 2);
    }

    [Fact]
    public void InspectReportsPlaceholdersAndCapacity()
    {
        var path = NewDoc((body, _) =>
        {
            for (var i = 1; i <= 4; i++) body.Append(P($"{{{{MO_{i}}}}} {{{{DI_{i}}}}}"));
            body.Append(P("{{SIGNATURE}}"));
        });
        var info = DocxTemplateEngine.Inspect(path);
        Assert.Equal(new Dictionary<string, int> { ["MO"] = 4, ["DI"] = 4 }, info.DayCapacity);
        Assert.True(info.HasSignature);
        Assert.False(info.HasEntriesBlock);
    }

    [Fact]
    public void InvalidFileIsRejected()
    {
        var bad = Path.Combine(_dir, "bad.docx");
        File.WriteAllBytes(bad, "not a zip file"u8.ToArray());
        Assert.Throws<TemplateError>(() => DocxTemplateEngine.ValidateDocxFile(bad));
        Assert.Throws<TemplateError>(() => DocxTemplateEngine.Render(bad, new()));
    }

    [Fact]
    public void StandardTemplateIsValid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "templates",
            "6bac038700f147698c836f5a4cdedcb2.docx");
        var info = DocxTemplateEngine.Inspect(path);
        foreach (var c in new[] { "MO", "DI", "MI", "DO", "FR" })
            Assert.Equal(6, info.DayCapacity[c]);
        Assert.True(info.HasSignature);
        Assert.Contains("NAME", info.Placeholders);
    }
}
