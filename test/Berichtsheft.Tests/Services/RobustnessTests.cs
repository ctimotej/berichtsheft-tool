using System.IO.Compression;
using Berichtsheft.Core;
using Berichtsheft.Core.Config;
using Berichtsheft.Core.Services;
using Berichtsheft.Core.Templating;
using DocumentFormat.OpenXml.Packaging;
using Xunit;

namespace Berichtsheft.Tests.Services;

public class RobustnessTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "bh-rob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Theory]
    [InlineData("a\u000Bb", "a\nb")]          // Word soft line break
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\u0001b", "ab")]             // control character
    [InlineData("Größe 😀\tok", "Größe 😀\tok")] // valid text stays
    public void CleanText_MakesTextXmlSafe(string input, string expected) =>
        Assert.Equal(expected, DocxTemplateEngine.CleanText(input));

    [Fact]
    public void CleanText_DropsLoneSurrogates() => Assert.Equal("xy", DocxTemplateEngine.CleanText("x" + (char)0xD800 + "y"));

    [Fact]
    public void Render_SurvivesControlCharactersInEntries()
    {
        var template = Path.Combine(AppContext.BaseDirectory, "Fixtures", "templates", "6bac038700f147698c836f5a4cdedcb2.docx");
        var bytes = DocxTemplateEngine.Render(template, new() { ["NAME"] = "A\u0001B", ["MO_1"] = "x\u000Bz" + (char)0xD800 });
        using var ms = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(ms, false);
        Assert.Contains("AB", doc.MainDocumentPart!.Document!.Body!.InnerText);
    }

    [Fact]
    public void TrainerBatch_CorruptFileOnlyFailsItself()
    {
        var dir = TempDir();
        try
        {
            var docx = Path.Combine(dir, "kaputt.docx"); File.WriteAllText(docx, "not a zip");
            var pdf = Path.Combine(dir, "kaputt.pdf"); File.WriteAllText(pdf, "not a pdf");
            var results = TrainerBatch.Process(new[] { docx, pdf }, new byte[] { 1 }, new DateOnly(2026, 9, 30), DateTime.Now);
            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.False(r.Ok));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void StageRestore_FailureLeavesNothingStaged()
    {
        var dir = TempDir();
        try
        {
            var cfg = new AppConfig { DataDir = Path.Combine(dir, "data") };
            var zipPath = Path.Combine(dir, "evil.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntry("berichtsheft.db").Open().Dispose();
                zip.CreateEntry(BackupService.InfoFile).Open().Dispose();
                zip.CreateEntry("reports/ok.txt").Open().Dispose();
                zip.CreateEntry("../../escape.txt").Open().Dispose();
            }
            Assert.ThrowsAny<Exception>(() => BackupService.StageRestore(cfg, zipPath));
            Assert.False(Directory.Exists(BackupService.StagingDir(cfg)));
            Assert.False(Directory.Exists(BackupService.StagingDir(cfg) + ".partial"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
