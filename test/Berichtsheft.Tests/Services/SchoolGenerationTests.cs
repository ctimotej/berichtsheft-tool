using System.Text.RegularExpressions;
using Berichtsheft.Core.Config;
using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Hosting;
using Berichtsheft.Core.Models;
using Berichtsheft.Core.Services;
using Berichtsheft.Tests.Hosting;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Berichtsheft.Tests.Services;

/// <summary>Berufsschule: Lernfelder LF1–LF12, Abteilung "Berufsschule" and the choice of Lernfelder per Ausbildungsjahr.</summary>
public class SchoolGenerationTests
{
    private static readonly Regex LfPrefix = new(@"^(LF\d+[a-d]?): ");

    private static void Setup(AppHost.Session s, string profession = "Fachinformatiker für Systemintegration") =>
        s.Settings.Update(new AppSettings { TraineeName = "Max", TrainingStartDate = new DateOnly(2026, 8, 1), Profession = profession });

    private static HashSet<string> Codes(ReportOut r) =>
        r.Days.SelectMany(d => d.Entries).Select(e => LfPrefix.Match(e.Text)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToHashSet();

    private static ReportOut SchoolWeek(AppHost.Session s, DateOnly monday, List<int>? subjects = null)
    {
        var unit = s.Units.SchoolUnitId()!.Value;
        var (r, _) = s.Reports.Generate(new GenerateRequest { StartDate = monday, UnitId = unit, Seed = 3, SchoolDepartmentIds = subjects });
        return s.Reports.ToOut(r);
    }

    [Fact]
    public void AllLernfelderAndTheAbteilungBerufsschuleAreSeeded()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        var codes = s.Catalog.ListDepartments(kind: "school").Select(d => d.Code).ToList();
        var expected = Enumerable.Range(1, 9).Select(i => $"LF{i}")
            .Concat(new[] { 10, 11, 12 }.SelectMany(n => "abcd".Select(c => $"LF{n}{c}"))).ToList();
        Assert.Equal(expected, codes); // in curriculum order: LF2 before LF10
        Assert.All(s.Catalog.ListDepartments(kind: "school"), d => Assert.True(d.ActiveSnippetCount >= 14));
        var school = Assert.Single(s.Units.List(), u => u.IsSchool);
        Assert.Equal("Berufsschule", school.Name);
    }

    [Theory]
    [InlineData(1, "Fachinformatiker für Systemintegration", "LF1 LF2 LF3 LF4 LF5")]
    [InlineData(2, "Fachinformatiker für Systemintegration", "LF6 LF7 LF8 LF9")]
    [InlineData(3, "Fachinformatiker für Systemintegration", "LF10b LF11b LF12b")]
    [InlineData(3, "Fachinformatiker für Anwendungsentwicklung", "LF10a LF11a LF12a")]
    [InlineData(3, "Fachinformatiker für Daten- und Prozessanalyse", "LF10c LF11c LF12c")]
    [InlineData(3, "Fachinformatiker für Digitale Vernetzung", "LF10d LF11d LF12d")]
    [InlineData(4, "Fachinformatiker für Digitale Vernetzung", "LF10d LF11d LF12d")] // extended training
    public void CurriculumPicksTheLernfelderOfTheYear(int year, string profession, string expected)
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        var subjects = s.Catalog.ListDepartments(onlyActive: true, kind: "school");
        var picked = Curriculum.ForYear(subjects, year, Curriculum.SpecializationOf(profession)).Select(d => d.Code);
        Assert.Equal(expected, string.Join(' ', picked));
    }

    [Fact]
    public void SchoolWeeksUseTheLernfelderOfTheirTrainingYear()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        Setup(s);
        var y1 = SchoolWeek(s, new DateOnly(2026, 9, 28));
        Assert.All(y1.Days, d => Assert.Equal("school", d.Type));
        Assert.Equal("Berufsschule", y1.DepartmentLabel);
        Assert.Equal("1", y1.TrainingYear);
        Assert.Subset(new HashSet<string> { "LF1", "LF2", "LF3", "LF4", "LF5" }, Codes(y1));
        Assert.True(Codes(y1).Count >= 4); // 15 topics spread evenly over the five Lernfelder
        Assert.All(y1.Days, d => Assert.Equal(3, d.Entries.Count));
        Assert.All(y1.Days, d => Assert.Equal(8, d.Entries.Sum(e => e.Hours)));

        var y2 = SchoolWeek(s, new DateOnly(2027, 9, 27));
        Assert.Subset(new HashSet<string> { "LF6", "LF7", "LF8", "LF9" }, Codes(y2));
        var y3 = SchoolWeek(s, new DateOnly(2028, 9, 25));
        Assert.Equal(new HashSet<string> { "LF10b", "LF11b", "LF12b" }, Codes(y3));
    }

    [Fact]
    public void ChosenLernfelderWinOverTheYear()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        Setup(s);
        var lf12d = s.Catalog.ListDepartments(kind: "school").Single(d => d.Code == "LF12d").Id;
        var r = SchoolWeek(s, new DateOnly(2026, 9, 28), new List<int> { lf12d });
        Assert.Equal(new HashSet<string> { "LF12d" }, Codes(r));
        Assert.All(r.Days, d => Assert.Equal(new[] { lf12d }, d.SubjectIds.ToArray()));
    }

    [Fact]
    public void WeekInfoPreselectsYearLernfelderAndTheSchoolAbteilungForSchoolBlocks()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        Setup(s, "Fachinformatiker für Anwendungsentwicklung");
        var byCode = s.Catalog.ListDepartments(kind: "school").ToDictionary(d => d.Code, d => d.Id);
        var info = s.Reports.WeekInfo(new DateOnly(2028, 10, 4));
        Assert.Equal(new[] { byCode["LF10a"], byCode["LF11a"], byCode["LF12a"] }, info.SchoolDepartmentIds.ToArray());

        // a Berufsschule block without Lernfelder: whole week school, Abteilung Berufsschule suggested
        s.Plan.Create(new PlanPeriodIn { Kind = "school", StartDate = new DateOnly(2026, 10, 5), EndDate = new DateOnly(2026, 10, 16) });
        var block = s.Reports.WeekInfo(new DateOnly(2026, 10, 7));
        Assert.All(block.DayTypes.Values, v => Assert.Equal("school", v));
        Assert.Equal(s.Units.SchoolUnitId(), block.PlanUnitId);
        Assert.Equal(Enumerable.Range(1, 5).Select(i => byCode[$"LF{i}"]), block.SchoolDepartmentIds);

        // generated from the plan alone (no Abteilung, no Themen): topics of year 1
        var (r, _) = s.Reports.Generate(new GenerateRequest { StartDate = new DateOnly(2026, 10, 12), Seed = 1 });
        var outp = s.Reports.ToOut(r);
        Assert.Equal("Berufsschule", outp.DepartmentLabel);
        Assert.Subset(new HashSet<string> { "LF1", "LF2", "LF3", "LF4", "LF5" }, Codes(outp));
    }

    [Fact]
    public void RotationToTheSchoolAbteilungMakesSchoolDaysAndBulkCreatesThem()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        Setup(s);
        s.Plan.Create(new PlanPeriodIn { Kind = "rotation", StartDate = new DateOnly(2027, 1, 4), EndDate = new DateOnly(2027, 1, 15), UnitId = s.Units.SchoolUnitId() });
        var res = s.Reports.Bulk(new BulkRequest { StartDate = new DateOnly(2027, 1, 4), EndDate = new DateOnly(2027, 1, 15), Seed = 2 }, dryRun: false);
        Assert.Equal(2, res.Created);
        Assert.All(res.Weeks, w => Assert.Equal(5, w.SchoolDays));
        foreach (var w in res.Weeks)
        {
            var r = s.Reports.ToOut(s.Reports.Get(w.ReportId!.Value));
            Assert.Equal("Berufsschule", r.DepartmentLabel);
            Assert.Subset(new HashSet<string> { "LF1", "LF2", "LF3", "LF4", "LF5" }, Codes(r));
        }
    }

    [Fact]
    public void MixedWeekKeepsSchoolTopicsWhenRegenerating()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        Setup(s);
        var unit = s.Units.List().First(u => !u.IsSchool);
        var monday = new DateOnly(2026, 11, 2);
        var (r, _) = s.Reports.Generate(new GenerateRequest
        {
            StartDate = monday, UnitId = unit.Id, DepartmentIds = unit.TopicIds, Seed = 5,
            DayTypes = new() { [monday.AddDays(3)] = "school", [monday.AddDays(4)] = "school" },
        });
        var before = s.Reports.ToOut(r);
        Assert.Equal(new[] { "work", "work", "work", "school", "school" }, before.Days.Select(d => d.Type));
        Assert.Equal(unit.Name, before.DepartmentLabel);
        var schoolBefore = before.Days.Where(d => d.Type == "school").SelectMany(d => d.Entries).Select(e => e.Text).ToList();
        Assert.All(schoolBefore, x => Assert.Matches(LfPrefix, x));

        s.Reports.Regenerate(r.Id, seed: 99);
        var after = s.Reports.ToOut(s.Reports.Get(r.Id));
        Assert.Equal(schoolBefore, after.Days.Where(d => d.Type == "school").SelectMany(d => d.Entries).Select(e => e.Text));
    }

    [Fact]
    public void EditorCanSwapTheLernfelderOfOneDay()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        Setup(s);
        var r = SchoolWeek(s, new DateOnly(2026, 9, 28));
        var lf9 = s.Catalog.ListDepartments(kind: "school").Single(d => d.Code == "LF9").Id;
        var entries = s.Reports.SchoolDayEntries(r.Id, new SchoolDayRequest { Date = new DateOnly(2026, 9, 30), SubjectIds = { lf9 }, Count = 4 });
        Assert.Equal(4, entries.Count);
        Assert.All(entries, e => Assert.StartsWith("LF9: ", e.Text));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }.Select(i => s.Catalog.ListDepartments(kind: "school").Single(d => d.Code == $"LF{i}").Id),
            s.Reports.SchoolSubjectsOf(r.Id, new DateOnly(2026, 9, 30)));
    }

    [Fact]
    public void SecondReportForTheSameWeekIsRefused()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        var topics = s.Catalog.ListDepartments(kind: "topic").Select(x => x.Id).ToList();
        s.Reports.Generate(new GenerateRequest { StartDate = new DateOnly(2026, 9, 28), DepartmentIds = topics });
        Assert.Throws<Berichtsheft.Core.InvalidInputError>(() =>
            s.Reports.Generate(new GenerateRequest { StartDate = new DateOnly(2026, 10, 1), DepartmentIds = topics }));
        Assert.NotNull(s.Reports.WeekInfo(new DateOnly(2026, 10, 2)).ExistingReportId);
    }

    [Fact]
    public void WithoutTrainingStartAllLernfelderAreUsedWithAHint()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        var (ids, hint) = s.Reports.SchoolSubjectsFor(new DateOnly(2026, 9, 28));
        Assert.Equal(s.Catalog.ActiveIds("school").Count, ids.Count);
        Assert.NotNull(hint);
    }

    [Fact]
    public void ThirdYearWithoutFachrichtungWarns()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        Setup(s, profession: "");
        var (ids, hint) = s.Reports.SchoolSubjectsFor(new DateOnly(2028, 10, 2));
        Assert.Equal(12, ids.Count); // LF10–12 of all four Fachrichtungen
        Assert.Contains("Fachrichtung", hint);
    }

    [Fact]
    public void ExportedSchoolWeekIsReadBackAsSchoolDays()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        Setup(s);
        var r = SchoolWeek(s, new DateOnly(2026, 9, 28));
        s.Reports.Export(r.Id);
        var week = Berichtsheft.Core.Templating.DocxReportReader.Read(s.Reports.Download(r.Id).Data, "x.docx");
        Assert.All(week.Days, d => Assert.Equal("school", d.Type));
        Assert.Equal(r.Days.SelectMany(d => d.Entries).Select(e => e.Text), week.Days.SelectMany(d => d.Entries).Select(e => e.Text));
        Assert.Equal("Berufsschule", week.DepartmentLabel);
    }

    [Theory]
    [InlineData("LF3", 3, null)]
    [InlineData("LF 10b", 10, 'b')]
    [InlineData("lf12D", 12, 'd')]
    [InlineData("Lernfeld 07", 7, null)]
    public void LernfeldCodesAreParsed(string code, int number, char? variant)
    {
        var p = Curriculum.Parse(code);
        Assert.NotNull(p);
        Assert.Equal(number, p!.Value.Number);
        Assert.Equal(variant, p.Value.Variant);
    }

    [Fact]
    public void LabelUsesTheDescriptionWhenTheNameIsOnlyTheCode()
    {
        var d = new DepartmentOut { Name = "LF10b", Code = "LF10b", Kind = "school", Description = "Serverdienste bereitstellen und Administrationsaufgaben automatisieren (3. Ausbildungsjahr, 80 Std.)" };
        Assert.Equal("LF10b: Serverdienste bereitstellen und Administrationsaufgaben automatisieren", Curriculum.Label(d));
        Assert.Null(Curriculum.Parse("WIKO"));
    }
}

public class DatabaseGuardTests
{
    [Fact]
    public void DatabaseOfAnUnknownVersionIsSetAsideNotChanged()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bh-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var db = Path.Combine(dir, "berichtsheft.db");
            using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "create table \"__EFMigrationsHistory\"(migration_id text primary key, product_version text); " +
                                  "insert into \"__EFMigrationsHistory\" values ('20250101000000_Old','8.0.10'); create table reports(id integer);";
                cmd.ExecuteNonQuery();
            }
            var cfg = AppConfig.Load(baseDir: dir, overrides: new Dictionary<string, string?> { ["DATA_DIR"] = dir, ["LOG_LEVEL"] = "Error" });
            using (var host = new AppHost(cfg))
            {
                Assert.NotNull(host.StartupNotice);
                using var s = host.Open();
                Assert.NotEmpty(s.Catalog.ListDepartments()); // fresh database with the example data
            }
            Assert.Single(Directory.GetFiles(dir, "berichtsheft.alt-*.db"));

            using (var again = new AppHost(cfg)) Assert.Null(again.StartupNotice); // our own database is kept
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }
}
