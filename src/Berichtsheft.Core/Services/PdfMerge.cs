using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Berichtsheft.Core.Services;

public static class PdfMerge
{
    /// <summary>All pages of the files, in order, as one PDF.</summary>
    public static byte[] Merge(IEnumerable<string> pdfPaths)
    {
        using var outDoc = new PdfDocument();
        foreach (var path in pdfPaths)
        {
            using var input = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            for (var i = 0; i < input.PageCount; i++) outDoc.AddPage(input.Pages[i]);
        }
        using var ms = new MemoryStream();
        outDoc.Save(ms, false);
        return ms.ToArray();
    }
}
