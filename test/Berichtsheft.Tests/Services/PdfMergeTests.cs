using Berichtsheft.Core.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Berichtsheft.Tests.Services;

public class PdfMergeTests
{
    private static string MakePdf(string dir, string name, int pages)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        var path = Path.Combine(dir, name);
        doc.Save(path);
        return path;
    }

    [Fact]
    public void MergesPagesInOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bh-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var merged = PdfMerge.Merge(new[] { MakePdf(dir, "a.pdf", 1), MakePdf(dir, "b.pdf", 3) });
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(merged, 0, 4));
            using var doc = PdfReader.Open(new MemoryStream(merged), PdfDocumentOpenMode.Import);
            Assert.Equal(4, doc.PageCount);
        }
        finally { Directory.Delete(dir, true); }
    }
}
