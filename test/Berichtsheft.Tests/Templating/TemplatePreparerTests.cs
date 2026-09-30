using Berichtsheft.Core.Templating;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Berichtsheft.Tests.Templating;

// Ported from tools/prepare_standard_template.py (no Python test existed; this checks the cell map)
public class TemplatePreparerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bh-prep-" + Guid.NewGuid().ToString("N"));

    public TemplatePreparerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private static TableCell Tc(int span = 1, int paragraphs = 1)
    {
        var tc = new TableCell();
        if (span > 1) tc.Append(new TableCellProperties(new GridSpan { Val = span }));
        for (var i = 0; i < paragraphs; i++) tc.Append(new Paragraph());
        return tc;
    }

    private static Table MakeTable(int rows, params (int row, int col, int span, int paras)[] special)
    {
        var t = new Table();
        for (var r = 0; r < rows; r++)
        {
            var tr = new TableRow();
            var cols = 13;
            var c = 0;
            while (c < cols)
            {
                var s = special.FirstOrDefault(x => x.row == r && x.col == c);
                var span = s.span == 0 ? 1 : s.span;
                tr.Append(Tc(span, s.paras == 0 ? 1 : s.paras));
                c += span;
            }
            t.Append(tr);
        }
        return t;
    }

    [Fact]
    public void InsertsPlaceholdersIntoExpectedCells()
    {
        var src = Path.Combine(_dir, "in.docx");
        using (var doc = WordprocessingDocument.Create(src, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            // header: cells 3-4 and 10-11 are merged, so grid column 10 is a later <w:tc> than index 10
            main.Document = new Document(new Body(
                MakeTable(4, (0, 3, 2, 1), (0, 10, 2, 1)),
                MakeTable(33, (32, 0, 1, 4), (32, 2, 1, 4), (32, 4, 1, 3))));
        }
        var dst = Path.Combine(_dir, "out.docx");
        TemplatePreparer.Prepare(src, dst);

        var info = DocxTemplateEngine.Inspect(dst);
        foreach (var d in new[] { "MO", "DI", "MI", "DO", "FR" }) Assert.Equal(6, info.DayCapacity[d]);
        Assert.True(info.HasSignature);
        foreach (var p in new[] { "NR", "NAME", "START_DATE", "END_DATE", "DEPARTMENT", "TRAINING_YEAR", "MO_1_H", "FR_6",
                     "SIGNATURE_DATE", "TRAINER_SIGNATURE", "TRAINER_SIGNATURE_DATE", "REMARKS" })
            Assert.Contains(p, info.Placeholders);

        using var check = WordprocessingDocument.Open(dst, false);
        var header = check.MainDocumentPart!.Document.Body!.Elements<Table>().First();
        var name = header.Elements<TableRow>().First().Elements<TableCell>().ElementAt(9); // grid col 10 = 10th tc
        Assert.Equal("{{NAME}}", name.InnerText);
    }
}
