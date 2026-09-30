using Berichtsheft.Core.Config;
using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Hosting;
using Berichtsheft.Core.Models;
using Berichtsheft.Tests.TestHelpers;
using Microsoft.Data.Sqlite;

namespace Berichtsheft.Tests.Hosting;

/// <summary>The services behind the desktop UI, run against a real temp data folder (SQLite, DOCX) - what
/// the removed HTTP tests used to cover through the API.</summary>
public sealed class TestHost : IDisposable
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "bh-test-" + Guid.NewGuid().ToString("N"));
    public AppHost Host { get; }

    public TestHost()
    {
        var cfg = AppConfig.Load(baseDir: DataDir, overrides: new Dictionary<string, string?> { ["DATA_DIR"] = DataDir, ["LOG_LEVEL"] = "Error" });
        Host = new AppHost(cfg);
    }

    public static string SignatureDataUrl() => "data:image/png;base64," + Convert.ToBase64String(SignatureFixtures.MakeSignaturePng());

    public void Dispose()
    {
        Host.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDir, true); } catch (IOException) { }
    }
}

public class HostTests
{
    [Fact]
    public void FirstStartSeedsCatalogAndTemplate()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        Assert.NotEmpty(s.Catalog.ListDepartments(kind: "topic"));
        Assert.NotEmpty(s.Catalog.ListDepartments(kind: "school"));
        Assert.NotEmpty(s.Templates.List());
        Assert.Contains(s.Templates.List(), x => x.IsActive);
    }

    [Fact]
    public void GenerateSignExportAndDelete()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        var topics = s.Catalog.ListDepartments(kind: "topic").Select(x => x.Id).ToList();
        var (r, _) = s.Reports.Generate(new GenerateRequest { StartDate = new DateOnly(2026, 9, 28), DepartmentIds = topics, Seed = 1 });
        Assert.Equal("draft", r.Status);
        Assert.Equal(5, s.Reports.ToOut(r).Days.Count);

        var (signed, _) = s.Reports.Sign(r.Id, new SignRequest { Image = TestHost.SignatureDataUrl(), SignerName = "Max" });
        Assert.Equal("signed", signed.Status);
        var (data, name) = s.Reports.Download(r.Id);
        Assert.True(data.Length > 1000);
        Assert.EndsWith(".docx", name);

        // editing voids the signature
        var days = System.Text.Json.JsonSerializer.Deserialize<List<ReportDay>>(System.Text.Json.JsonSerializer.Serialize(s.Reports.ToOut(r).Days))!; // a copy, like the UI edits
        days[0].Entries[0].Text = "Geändert";
        s.Reports.Update(r.Id, new ReportUpdate { Days = days });
        Assert.Empty(s.Reports.Get(r.Id).Signatures);

        s.Reports.Delete(r.Id);
        Assert.Empty(s.Reports.List());
    }

    [Fact]
    public void SignedReportCanBeSavedAsPdfAndZipOfPdfs()
    {
        using var t = new TestHost();
        if (!t.Host.Context.Converter.PdfAvailable) return; // needs LibreOffice or Word on the machine
        using var s = t.Host.Open();
        var topics = s.Catalog.ListDepartments(kind: "topic").Select(x => x.Id).ToList();
        var ids = new[] { new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 5) }
            .Select(d => s.Reports.Generate(new GenerateRequest { StartDate = d, DepartmentIds = topics, Seed = 3 }).Report.Id).ToList();
        s.Reports.Sign(ids[0], new SignRequest { Image = TestHost.SignatureDataUrl(), SignerName = "Max" });

        var (pdf, name) = s.Reports.DownloadPdf(ids[0]);
        Assert.EndsWith(".pdf", name);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));

        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(s.Reports.ZipDocuments(ids, pdf: true)));
        Assert.Equal(2, zip.Entries.Count);
        Assert.All(zip.Entries, e => Assert.EndsWith(".pdf", e.FullName));
    }

    [Fact]
    public void BackupAndRestoreBringBackReports()
    {
        using var t = new TestHost();
        var zip = Path.Combine(Path.GetTempPath(), "bh-backup-test-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var s = t.Host.Open())
            {
                var topics = s.Catalog.ListDepartments(kind: "topic").Select(x => x.Id).ToList();
                s.Reports.Generate(new GenerateRequest { StartDate = new DateOnly(2026, 9, 28), DepartmentIds = topics, Seed = 5 });
            }
            Berichtsheft.Core.Services.BackupService.Create(t.Host.Config, zip, "test");

            // "another PC": fresh data folder, restore staged and applied like at the next start
            var other = Path.Combine(Path.GetTempPath(), "bh-restore-" + Guid.NewGuid().ToString("N"));
            var cfg = AppConfig.Load(baseDir: other, overrides: new Dictionary<string, string?> { ["DATA_DIR"] = other, ["LOG_LEVEL"] = "Error" });
            using (var h = new AppHost(cfg)) { using var s = h.Open(); Assert.Empty(s.Reports.List()); }
            SqliteConnection.ClearAllPools();
            Berichtsheft.Core.Services.BackupService.StageRestore(cfg, zip);
            var kept = Berichtsheft.Core.Services.BackupService.ApplyPendingRestore(cfg);
            Assert.NotNull(kept);
            using (var h = new AppHost(cfg)) { using var s = h.Open(); Assert.Single(s.Reports.List()); }
            SqliteConnection.ClearAllPools();
            foreach (var d in new[] { other, kept! }) try { Directory.Delete(d, true); } catch (IOException) { }
        }
        finally { try { File.Delete(zip); } catch (IOException) { } }
    }

    [Fact]
    public void WordFileOfTheStandardFormIsReadBackAndImported()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        var unit = s.Units.List().First();
        var (orig, _) = s.Reports.Generate(new GenerateRequest
        {
            StartDate = new DateOnly(2026, 9, 28), UnitId = unit.Id, DepartmentIds = unit.TopicIds, Seed = 4,
            DayTypes = new() { [new DateOnly(2026, 9, 30)] = "vacation" },
        });
        s.Reports.Export(orig.Id);
        var (docx, _) = s.Reports.Download(orig.Id);
        var expected = s.Reports.ToOut(orig);

        var week = Berichtsheft.Core.Templating.DocxReportReader.Read(docx, "test.docx");
        Assert.Equal(new DateOnly(2026, 9, 28), week.StartDate);
        Assert.Equal(orig.ReportNumber, week.ReportNumber);
        Assert.Equal(unit.Name, week.DepartmentLabel);
        for (var d = 0; d < 5; d++)
        {
            var want = expected.Days[d].Entries;
            Assert.Equal(want.Select(e => e.Text), week.Days[d].Entries.Select(e => e.Text));
            Assert.Equal(want.Select(e => e.Hours), week.Days[d].Entries.Select(e => e.Hours));
        }
        Assert.Equal("vacation", week.Days[2].Type);

        // importing it again is refused while the week exists; after deleting it works and nothing is regenerated
        Assert.Throws<Berichtsheft.Core.InvalidInputError>(() => s.Reports.ImportWeek(week));
        s.Reports.Delete(orig.Id);
        var (imported, _) = s.Reports.ImportWeek(week);
        Assert.Equal(expected.Days.SelectMany(x => x.Entries).Select(e => e.Text), s.Reports.ToOut(imported).Days.SelectMany(x => x.Entries).Select(e => e.Text));
        Assert.Equal(unit.Id, imported.UnitId);
        Assert.Throws<Berichtsheft.Core.InvalidInputError>(() => s.Reports.Regenerate(imported.Id));
        s.Reports.Sign(imported.Id, new SignRequest { Image = TestHost.SignatureDataUrl(), SignerName = "Max" }); // features work on imported reports
    }

    [Fact]
    public void ImportedWeekWithoutHoursGetsThemDistributed()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        var week = Berichtsheft.Core.Dtos.ImportWeek.Blank(new DateOnly(2026, 10, 7)); // a Wednesday
        week.DepartmentLabel = "Service";
        week.Days[0].Entries.Add(new() { Text = "Ticket bearbeitet" });
        week.Days[0].Entries.Add(new() { Text = "Client eingerichtet" });
        week.Days[1].Type = "vacation";
        var (r, _) = s.Reports.ImportWeek(week);
        var days = s.Reports.ToOut(r).Days;
        Assert.Equal(new DateOnly(2026, 10, 5), r.StartDate);
        Assert.Equal(8, days[0].Entries.Sum(e => e.Hours));
        Assert.Equal("Urlaub", days[1].Entries.Single().Text);
        Assert.Throws<Berichtsheft.Core.InvalidInputError>(() => s.Reports.ImportWeek(Berichtsheft.Core.Dtos.ImportWeek.Blank(new DateOnly(2026, 10, 8))));
    }

    [Fact]
    public void TrainerSignatureLandsInTheAusbilderFieldOfWordAndPdf()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        var unit = s.Units.List().First();
        var (r, _) = s.Reports.Generate(new GenerateRequest { StartDate = new DateOnly(2026, 9, 28), UnitId = unit.Id, DepartmentIds = unit.TopicIds, Seed = 6 });
        s.Reports.Sign(r.Id, new SignRequest { Image = TestHost.SignatureDataUrl(), SignerName = "Max" }); // trainee signed already
        var (docx, _) = s.Reports.Download(r.Id);
        var sig = SignatureFixtures.MakeSignaturePng();
        var day = new DateOnly(2026, 10, 2);
        var outDir = Environment.GetEnvironmentVariable("BH_TEST_OUT");

        // Word: a picture appears in the cell of "Ausbildender bzw. Ausbilder", above the underscores, plus the date
        var signedDocx = Berichtsheft.Core.Services.TrainerSigner.SignDocx(docx, sig, day);
        using (var ms = new MemoryStream(signedDocx))
        using (var wd = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(ms, false))
        {
            var cell = wd.MainDocumentPart!.Document.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.TableCell>()
                .Last(c => c.InnerText.Contains("Ausbilder") && c.InnerText.Contains("Unterschrift"));
            Assert.Single(cell.Descendants<DocumentFormat.OpenXml.Wordprocessing.Drawing>());
            Assert.Contains("02.10.2026", cell.InnerText);
        }
        Assert.Throws<Berichtsheft.Core.TemplateError>(() => Berichtsheft.Core.Services.TrainerSigner.SignDocx(signedDocx, sig, day)); // no double signing
        if (outDir != null) File.WriteAllBytes(Path.Combine(outDir, "trainer-signed.docx"), signedDocx);

        // PDF: needs LibreOffice/Word to make one
        if (!t.Host.Context.Converter.PdfAvailable) return;
        var pdf = t.Host.Context.Converter.ToPdf(docx);
        var signedPdf = Berichtsheft.Core.Services.TrainerSigner.SignPdf(pdf, sig, day);
        using (var d0 = UglyToad.PdfPig.PdfDocument.Open(pdf)) using (var d1 = UglyToad.PdfPig.PdfDocument.Open(signedPdf))
        {
            var before = d0.GetPage(1).GetImages().Count();
            var added = d1.GetPage(1).GetImages().ToList();
            Assert.Equal(before + 1, added.Count);
            var label = d1.GetPage(1).GetWords().First(w => w.Text.StartsWith("Ausbildender"));
            var box = added.OrderBy(i => i.BoundingBox.Left).Last().BoundingBox;              // the new picture sits in the Ausbilder column ...
            Assert.InRange((box.Left + box.Right) / 2, label.BoundingBox.Left - 20, label.BoundingBox.Right + 120);
            Assert.True(box.Bottom > label.BoundingBox.Top);                        // ... above the label
        }
        Assert.Throws<Berichtsheft.Core.TemplateError>(() => Berichtsheft.Core.Services.TrainerSigner.SignPdf(signedPdf, sig, day));
        if (outDir != null) { File.WriteAllBytes(Path.Combine(outDir, "trainer-signed.pdf"), signedPdf); File.WriteAllBytes(Path.Combine(outDir, "trainee-only.pdf"), pdf); }
    }

    [Fact]
    public void TrainerBatchSignsDroppedFilesIntoATimestampedSubfolder()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        var unit = s.Units.List().First();
        var (r, _) = s.Reports.Generate(new GenerateRequest { StartDate = new DateOnly(2026, 9, 28), UnitId = unit.Id, DepartmentIds = unit.TopicIds, Seed = 7 });
        s.Reports.Export(r.Id);
        var (docx, _) = s.Reports.Download(r.Id);
        var inbox = Path.Combine(t.DataDir, "inbox");
        Directory.CreateDirectory(inbox);
        File.WriteAllBytes(Path.Combine(inbox, "KW40.docx"), docx);
        File.WriteAllText(Path.Combine(inbox, "notiz.txt"), "x");
        File.WriteAllBytes(Path.Combine(inbox, "kaputt.pdf"), new byte[] { 1, 2, 3 });
        var sig = SignatureFixtures.MakeSignaturePng();
        var now = new DateTime(2026, 10, 5, 14, 30, 0);

        var res = Berichtsheft.Core.Services.TrainerBatch.Process(new[] { inbox }, sig, new DateOnly(2026, 10, 5), now, alsoPdf: true, t.Host.Context.Converter);
        var byName = res.ToDictionary(x => Path.GetFileName(x.Source));
        Assert.Equal(new[] { "KW40.docx", "kaputt.pdf" }, byName.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray()); // the .txt is not even picked up
        Assert.True(byName["KW40.docx"].Ok);
        Assert.False(byName["kaputt.pdf"].Ok);                                                                                    // one broken file does not stop the rest
        var outDir = Path.Combine(inbox, "Berichtsheft-signiert-2026-10-05_14-30");
        Assert.True(File.Exists(Path.Combine(outDir, "KW40.docx")));
        if (t.Host.Context.Converter.PdfAvailable) Assert.True(File.Exists(Path.Combine(outDir, "KW40.pdf")));
        Assert.Equal(docx.Length > 0, File.ReadAllBytes(Path.Combine(inbox, "KW40.docx")).SequenceEqual(docx)); // original untouched

        // the same batch again in the same minute: no overwriting
        Berichtsheft.Core.Services.TrainerBatch.Process(new[] { Path.Combine(inbox, "KW40.docx") }, sig, new DateOnly(2026, 10, 5), now);
        Assert.True(File.Exists(Path.Combine(outDir, "KW40_2.docx")));
    }

    [Fact]
    public void HoursCheckFlagsDaysThatDoNotAddUpAndRedistributes()
    {
        var cfg = new Berichtsheft.Core.Models.AppSettings { HoursPerDay = 8, SchoolHoursPerDay = 7 };
        ReportDay Day(int n, string type, params double?[] hours) => new()
        {
            Date = new DateOnly(2026, 9, 28).AddDays(n), Type = type,
            Entries = hours.Select((h, k) => new ReportEntry { Text = "E" + k, Hours = h }).ToList(),
        };
        var days = new List<ReportDay>
        {
            Day(0, "work", 4, 4),          // ok
            Day(1, "work", 3, 4.5),        // 7.5
            Day(2, "school", 7),           // ok (school hours)
            Day(3, "work", null, null),    // no hours at all
            Day(4, "vacation", new double?[] { null }), // no expectation
        };
        var issues = Berichtsheft.Core.Services.HoursCheck.Check(days, cfg);
        Assert.Equal(new[] { 1, 3 }, issues.Select(x => x.DayIndex).ToArray());
        Assert.Contains("7,5 Std. statt 8", issues[0].Message);

        Berichtsheft.Core.Services.HoursCheck.Redistribute(days, cfg, issues.Select(x => x.DayIndex));
        Assert.Empty(Berichtsheft.Core.Services.HoursCheck.Check(days, cfg));
        Assert.Equal(8, days[3].Entries.Sum(e => e.Hours));
    }

    [Fact]
    public void BulkActionSignsWithDefaultSignatureAndReportsFailures()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        var topics = s.Catalog.ListDepartments(kind: "topic").Select(x => x.Id).ToList();
        var ids = new[] { new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 5) }
            .Select(d => s.Reports.Generate(new GenerateRequest { StartDate = d, DepartmentIds = topics, Seed = 2 }).Report.Id).ToList();

        var noSig = s.Reports.BulkAction(ids, "sign");
        Assert.Equal(0, noSig.Done);
        Assert.Equal(2, noSig.Failed.Count);

        s.Settings.SetDefaultSignature(SignatureFixtures.MakeSignaturePng());
        var ok = s.Reports.BulkAction(ids, "sign");
        Assert.Equal(2, ok.Done);
        Assert.All(s.Reports.List(), r => Assert.True(r.Signed));
    }

    [Fact]
    public void PlanRotationPreselectsTopicsAndSchoolBlockSetsDayTypes()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        var topics = s.Catalog.ListDepartments(kind: "topic").Select(x => x.Id).ToList();
        s.Plan.Create(new PlanPeriodIn { Kind = "rotation", StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 10, 31), DepartmentIds = { topics[0] } });
        s.Plan.Create(new PlanPeriodIn { Kind = "vacation", StartDate = new DateOnly(2026, 9, 30), EndDate = new DateOnly(2026, 10, 1) });
        var info = s.Reports.WeekInfo(new DateOnly(2026, 9, 30));
        Assert.Equal(new[] { topics[0] }, info.PlanDepartmentIds.ToArray());
        Assert.Equal("vacation", info.DayTypes[new DateOnly(2026, 9, 30)]);
        Assert.Equal("work", info.DayTypes[new DateOnly(2026, 9, 28)]);
    }

    [Fact]
    public void SettingsFromOlderVersionsWithExchangeFieldsStillLoad()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        s.Settings.SetFlag("app_settings", System.Text.Json.JsonDocument.Parse(
            """{"mode":"azubi","exchange_dir":"X:\\share","trainee_name":"Max","trainer_keys":["a"]}""").RootElement);
        var cfg = s.Settings.Get();
        Assert.Equal("Max", cfg.TraineeName);
    }

    [Fact]
    public void Coverage_countsSignedWeeksAsDone()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        s.Settings.Update(new AppSettings { TraineeName = "Max", TrainingStartDate = new DateOnly(2026, 9, 1) });
        var topics = s.Catalog.ListDepartments(kind: "topic").Select(x => x.Id).ToList();
        var rep = s.Reports.Generate(new GenerateRequest { StartDate = new DateOnly(2026, 9, 7), DepartmentIds = topics, Seed = 1 }).Report;
        s.Reports.Sign(rep.Id, new SignRequest { Image = TestHost.SignatureDataUrl() });
        var cov = s.Reports.Coverage(new DateOnly(2026, 9, 21));
        Assert.Contains(cov.Weeks, w => w.ReportId == rep.Id && w.Status == "signed" && !w.Overdue);
        Assert.True(cov.Missing >= 1);
    }
}
