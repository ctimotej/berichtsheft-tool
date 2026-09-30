using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Berichtsheft.Core.Data;
using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Generation;
using Berichtsheft.Core.Models;
using Berichtsheft.Core.Signature;
using Berichtsheft.Core.Storage;
using Berichtsheft.Core.Templating;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Berichtsheft.Core.Services;

/// <summary>Report workflow: generate -> review/edit -> export DOCX -> sign -> save.
/// Ported from app/services/report_service.py.</summary>
public sealed class ReportService
{
    public BerichtsheftDbContext Db { get; }
    public BerichtsheftContext Ctx { get; }
    public CatalogService Catalog { get; }
    public SettingsService SettingsSvc { get; }
    public TemplateService Templates { get; }
    public PlanService Plan { get; }
    public UnitService Units { get; }
    private readonly ILogger _log;

    public ReportService(BerichtsheftDbContext db, BerichtsheftContext ctx)
    {
        Db = db; Ctx = ctx;
        Catalog = new CatalogService(db);
        SettingsSvc = new SettingsService(db);
        Templates = new TemplateService(db, ctx);
        Plan = new PlanService(db);
        Units = new UnitService(db);
        _log = ctx.Log<ReportService>();
    }

    public static string ComputeReportNumber(DateOnly? trainingStart, DateOnly monday)
    {
        if (trainingStart is not { } ts) return "";
        var tsMonday = Scheduling.WeekBounds(ts).Monday;
        var n = (monday.DayNumber - tsMonday.DayNumber) / 7 + 1; // exact multiple of 7, no rounding issues
        return n >= 1 ? n.ToString(CultureInfo.InvariantCulture) : "";
    }

    private static (int Year, int Week) Iso(DateOnly d)
    {
        var dt = d.ToDateTime(TimeOnly.MinValue);
        return (ISOWeek.GetYear(dt), ISOWeek.GetWeekOfYear(dt));
    }

    // ================================================================ read
    public ReportEntity Get(int id) =>
        Db.Reports.Include(r => r.Signatures).FirstOrDefault(r => r.Id == id) ?? throw new NotFoundError("Der Bericht wurde nicht gefunden.");

    public static List<ReportDay> DaysOf(ReportEntity r) => r.Content?.Days ?? new();

    public ReportOut ToOut(ReportEntity r)
    {
        var days = DaysOf(r);
        var docName = (r.DocumentKey ?? "").Split('/').Last();
        return new ReportOut
        {
            Id = r.Id, 
            PeriodType = r.PeriodType, StartDate = r.StartDate, EndDate = r.EndDate, CalendarWeek = r.CalendarWeek, Year = r.Year,
            ReportNumber = r.ReportNumber, TraineeName = r.TraineeName, TrainingYear = r.TrainingYear, DepartmentLabel = r.DepartmentLabel,
            UnitId = r.UnitId, DepartmentIds = r.DepartmentIds ?? new(), GenerationOptions = r.GenerationOptions ?? new(),
            Days = days, Remarks = r.Remarks, Status = r.Status, TemplateId = r.TemplateId,
            HasDocument = !string.IsNullOrEmpty(r.DocumentKey), DocumentName = docName.Length > 0 ? docName : null,
            ExportedAt = r.ExportedAt,
            Signatures = r.Signatures.Select(s => new SignatureInfo { Id = s.Id, Role = s.Role, SignerName = s.SignerName, Provider = s.Provider, SignedAt = s.SignedAt }).ToList(),
            TotalHours = days.SelectMany(d => d.Entries).Sum(e => e.Hours ?? 0),
            CreatedAt = r.CreatedAt, UpdatedAt = r.UpdatedAt,
        };
    }

    public List<ReportSummary> List(int limit = 200)
    {
        var rows = Db.Reports.Include(r => r.Signatures).AsEnumerable().OrderByDescending(r => r.StartDate).ThenByDescending(r => r.Id).Take(limit);
        var cfg = SettingsSvc.Get();
        return rows.Select(r => new ReportSummary
        {
            Id = r.Id, StartDate = r.StartDate, EndDate = r.EndDate, CalendarWeek = r.CalendarWeek, Year = r.Year,
            ReportNumber = r.ReportNumber, DepartmentLabel = r.DepartmentLabel, Status = r.Status,
            EntryCount = DaysOf(r).Sum(d => d.Entries.Count), TotalHours = DaysOf(r).SelectMany(d => d.Entries).Sum(e => e.Hours ?? 0),
            HoursIssues = HoursCheck.Check(DaysOf(r), cfg).Count, HasDocument = !string.IsNullOrEmpty(r.DocumentKey),
            UpdatedAt = r.UpdatedAt, TrainingYear = r.TrainingYear ?? "",
            Signed = r.Signatures.Any(s => s.Role == "trainee"),
        }).ToList();
    }

    public WeekInfo WeekInfo(DateOnly anyDay)
    {
        var cfg = SettingsSvc.Get();
        var (monday, friday) = Scheduling.WeekBounds(anyDay);
        var iso = Iso(monday);
        int? existing = Db.Reports.Where(r => r.StartDate == monday).Select(r => (int?)r.Id).AsEnumerable().FirstOrDefault();
        var holidays = Holidays.HolidaysBetween(monday, friday, cfg.FederalState);
        var (planTypes, lernfelder) = Plan.DayPlan(monday, friday);
        var (planUnit, planTopics) = Plan.Rotation(monday, friday);
        var dayTypes = new Dictionary<DateOnly, string>();
        for (var i = 0; i < 5; i++)
        {
            var d = monday.AddDays(i);
            dayTypes[d] = holidays.ContainsKey(d) ? "holiday" : planTypes.GetValueOrDefault(d, "work");
        }
        var number = ComputeReportNumber(cfg.TrainingStartDate, monday);
        return new WeekInfo
        {
            StartDate = monday, EndDate = friday, CalendarWeek = iso.Week, Year = iso.Year,
            ReportNumber = number.Length > 0 ? number : NextNumber(),
            TrainingYear = CoverageService.ComputeTrainingYear(cfg.TrainingStartDate, monday),
            ExistingReportId = existing, Holidays = holidays, DayTypes = dayTypes,
            PlanUnitId = planUnit, PlanDepartmentIds = planTopics, SchoolDepartmentIds = lernfelder,
        };
    }

    public CoverageOut Coverage(DateOnly? today = null)
    {
        var cfg = SettingsSvc.Get();
        var rows = Db.Reports.Select(r => new { r.StartDate, r.Id, r.Status, r.ReportNumber }).AsEnumerable().ToList();
        var reports = new Dictionary<DateOnly, (int, string, string)>();
        foreach (var r in rows) reports[Scheduling.WeekBounds(r.StartDate).Monday] = (r.Id, r.Status, r.ReportNumber);
        var done = new HashSet<string> { "signed" };
        return CoverageService.ComputeCoverage(cfg.TrainingStartDate, cfg.TrainingEndDate, today ?? DateOnly.FromDateTime(DateTime.Today),
            reports, done, cfg.OverdueDays, rows.Count > 0 ? rows.Min(r => r.StartDate) : null);
    }

    private string NextNumber()
    {
        var last = Db.Reports.OrderByDescending(r => r.StartDate).Select(r => r.ReportNumber).AsEnumerable().FirstOrDefault();
        return last != null && last.Length > 0 && last.All(char.IsAsciiDigit) ? (int.Parse(last) + 1).ToString() : "";
    }

    // ================================================================ generate
    private HashSet<int> RecentSnippetIds(AppSettings cfg, DateOnly before)
    {
        if (cfg.AvoidRecentReports <= 0) return new();
        var rows = Db.Reports.Where(r => r.StartDate < before).OrderByDescending(r => r.StartDate).Take(cfg.AvoidRecentReports).ToList();
        return rows.SelectMany(DaysOf).SelectMany(d => d.Entries).Where(e => e.SnippetId is > 0).Select(e => e.SnippetId!.Value).ToHashSet();
    }

    private Dictionary<string, int> DayCapacity()
    {
        try { return TemplateService.ToOut(Templates.GetActive()).DayCapacity; }
        catch (AppError) { return new(); }
    }

    public (List<ReportDay> Days, DateOnly Start, DateOnly End, List<string> Warnings) BuildDays(GenerateRequest req, AppSettings cfg)
    {
        DateOnly start, end;
        if (req.EndDate is null) (start, end) = Scheduling.WeekBounds(req.StartDate);
        else (start, end) = (req.StartDate, req.EndDate.Value);
        var dates = Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(i => start.AddDays(i))
            .Where(d => IsWeekday(d) || req.DayTypes.ContainsKey(d)).ToList();
        if (dates.Count == 0) throw new InvalidInputError("Der gewählte Zeitraum enthält keine Arbeitstage.");
        var holidays = Holidays.HolidaysBetween(start, end, cfg.FederalState);
        var (planTypes, planLernfelder) = Plan.DayPlan(start, end);
        var dayTypes = dates.ToDictionary(d => d, d =>
            req.DayTypes.TryGetValue(d, out var t) && t.Length > 0 ? t : holidays.ContainsKey(d) ? "holiday" : planTypes.GetValueOrDefault(d, "work"));
        var workDays = dates.Where(d => dayTypes[d] == "work").ToList();
        var schoolDays = dates.Where(d => dayTypes[d] == "school").ToList();

        var warnings = new List<string>();
        var recent = req.AvoidRecent ? RecentSnippetIds(cfg, start) : new HashSet<int>();
        var schoolEntries = new Dictionary<DateOnly, List<ReportEntry>>();
        var defaultSubjects = req.SchoolDepartmentIds ?? planLernfelder;
        var daySubjects = schoolDays.ToDictionary(d => d, d =>
            (req.DaySubjects.TryGetValue(d, out var s) && s.Count > 0 ? s : defaultSubjects ?? new()).ToList());
        if (schoolDays.Count > 0 && cfg.SchoolTopics)
        {
            var groups = new List<(List<int> Subjects, List<DateOnly> Days)>();
            foreach (var d in schoolDays) // days with the same subjects share one pool (no repeats)
            {
                var g = groups.FirstOrDefault(x => x.Subjects.SequenceEqual(daySubjects[d]));
                if (g.Days == null) { g = (daySubjects[d], new List<DateOnly>()); groups.Add(g); }
                g.Days.Add(d);
            }
            foreach (var (subjects, groupDays) in groups)
            {
                var (got, w) = SchoolEntries(groupDays, subjects, cfg, recent, req.Seed, req.FillHours);
                foreach (var (k, v) in got) schoolEntries[k] = v;
                warnings.AddRange(w.Where(x => !warnings.Contains(x)));
            }
        }
        IEnumerator<GeneratedEntry> entries = new List<GeneratedEntry>().GetEnumerator();
        Dictionary<DateOnly, int> perDay = new();
        if (workDays.Count > 0)
        {
            var counts = req.CountMode == "per_day"
                ? Scheduling.RangeCounts(req.Count, req.CountMax, workDays.Count, req.Seed.HasValue ? new Random(req.Seed.Value) : new Random())
                : Scheduling.SplitCounts(req.Count, workDays.Count);
            if (req.DepartmentIds.Count == 0) throw new InvalidInputError("Bitte mindestens ein Thema auswählen.");
            var (pool, _) = Catalog.BuildPool(req.DepartmentIds);
            var opts = new GenerationOptions
            {
                AvoidDuplicates = req.AvoidDuplicates, EvenDistribution = req.EvenDistribution, AvoidSnippetIds = recent, Seed = req.Seed,
            };
            var result = Ctx.Generator.Generate(pool, counts.Sum(), opts);
            warnings.AddRange(result.Warnings);
            entries = result.Entries.GetEnumerator();
            for (var i = 0; i < workDays.Count; i++) perDay[workDays[i]] = counts[i];
            var cap = DayCapacity();
            if (cap.Count > 0 && counts.Max() > cap.Values.Min())
                warnings.Add($"Die aktive Vorlage hat nur {cap.Values.Min()} Zeilen pro Tag; überzählige Einträge werden in der letzten Zeile zusammengefasst.");
        }

        var days = new List<ReportDay>();
        foreach (var d in dates)
        {
            var t = dayTypes[d];
            List<ReportEntry> list;
            if (t == "work")
            {
                var picked = new List<GeneratedEntry>();
                for (var i = 0; i < perDay[d]; i++) { entries.MoveNext(); picked.Add(entries.Current); }
                var hours = req.FillHours ? Scheduling.DistributeHours(cfg.HoursPerDay, picked.Count).Select(h => (double?)h).ToList()
                                          : picked.Select(_ => (double?)null).ToList();
                list = picked.Select((e, i) => new ReportEntry { Text = e.Text, Hours = hours[i] is > 0 ? hours[i] : null, SnippetId = e.SnippetId, DepartmentId = e.DepartmentId }).ToList();
            }
            else if (t == "school" && schoolEntries.TryGetValue(d, out var se) && se.Count > 0) list = se;
            else
            {
                var label = t == "holiday" ? holidays.GetValueOrDefault(d, Scheduling.DayTypes[t]) : Scheduling.DayTypes[t];
                double? hours = t == "school" && req.FillHours ? cfg.SchoolHoursPerDay : null;
                list = new() { new ReportEntry { Text = label, Hours = hours is > 0 ? hours : null } };
            }
            days.Add(new ReportDay { Date = d, Type = t, Entries = list, SubjectIds = daySubjects.GetValueOrDefault(d) ?? new() });
        }
        return (days, start, end, warnings);
    }

    private static bool IsWeekday(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>Lesson topics of the given subjects (or all active ones) for each school day, spread evenly over the subjects.</summary>
    private (Dictionary<DateOnly, List<ReportEntry>> Entries, List<string> Warnings) SchoolEntries(
        List<DateOnly> schoolDays, List<int> subjectIds, AppSettings cfg, HashSet<int> recent, int? seed, bool fillHours,
        HashSet<int>? exclude = null, int? perDay = null)
    {
        var ids = subjectIds.Count > 0 ? subjectIds : Catalog.ActiveIds("school");
        if (ids.Count == 0) return (new(), new());
        var (pool, _) = Catalog.BuildPool(ids, strict: false);
        if (exclude is { Count: > 0 })
            pool = pool.ToDictionary(kv => kv.Key, kv =>
            {
                var f = kv.Value.Where(c => !exclude.Contains(c.Id)).ToList();
                return f.Count > 0 ? f : kv.Value;
            });
        var n = perDay ?? cfg.SchoolEntriesPerDay;
        var total = n * schoolDays.Count;
        var warnings = new List<string>();
        GenerationResult result;
        try
        {
            result = Ctx.Generator.Generate(pool, total, new GenerationOptions { AvoidDuplicates = true, EvenDistribution = true, AvoidSnippetIds = recent, Seed = seed });
        }
        catch (NotEnoughSnippetsError)
        {
            if (!pool.Values.Any(v => v.Count > 0))
                return (new(), new() { "Für die gewählten Fächer sind keine Themen hinterlegt – Schultage enthalten nur „Berufsschule“." });
            result = Ctx.Generator.Generate(pool, total, new GenerationOptions { AvoidDuplicates = false, EvenDistribution = true, AvoidSnippetIds = recent, Seed = seed });
            warnings.Add("Zu wenige Themen für die Schultage – einige Themen kommen mehrfach vor.");
        }
        var outp = new Dictionary<DateOnly, List<ReportEntry>>();
        var idx = 0;
        foreach (var d in schoolDays)
        {
            var picked = result.Entries.Skip(idx).Take(n).ToList(); idx += n;
            var hours = fillHours ? Scheduling.DistributeHours(cfg.SchoolHoursPerDay, n).Select(h => (double?)h).ToList() : picked.Select(_ => (double?)null).ToList();
            outp[d] = picked.Select((e, i) => new ReportEntry { Text = e.Text, Hours = hours[i] is > 0 ? hours[i] : null, SnippetId = e.SnippetId, DepartmentId = e.DepartmentId }).ToList();
        }
        return (outp, warnings);
    }

    /// <summary>New topics for one school day from the chosen subjects (editor: 'Fächer wählen').</summary>
    public List<ReportEntry> SchoolDayEntries(int reportId, SchoolDayRequest req)
    {
        var r = Get(reportId);
        if (!(r.StartDate <= req.Date && req.Date <= r.EndDate)) throw new InvalidInputError("Der Tag liegt außerhalb des Berichtszeitraums.");
        var deps = Catalog.ListDepartments(kind: "school").ToDictionary(d => d.Id);
        if (req.SubjectIds.Any(i => !deps.ContainsKey(i))) throw new InvalidInputError("Mindestens ein gewähltes Fach existiert nicht (mehr).");
        var cfg = SettingsSvc.Get();
        var fill = Opt(r, "fill_hours", cfg.FillHours);
        var (outp, _) = SchoolEntries(new() { req.Date }, req.SubjectIds, cfg, new(), null, fill, req.ExcludeSnippetIds.ToHashSet(), req.Count);
        if (outp.Count == 0) throw new InvalidInputError("Für die gewählten Fächer sind keine Themen hinterlegt.");
        return outp[req.Date];
    }

    private static T Opt<T>(ReportEntity r, string key, T dflt)
    {
        if (r.GenerationOptions != null && r.GenerationOptions.TryGetValue(key, out var el) && el.ValueKind != JsonValueKind.Null)
        {
            try { return el.Deserialize<T>(Json.Options) ?? dflt; } catch (JsonException) { }
        }
        return dflt;
    }

    public (ReportEntity Report, List<string> Warnings) Generate(GenerateRequest req)
    {
        var cfg = SettingsSvc.Get();
        var (days, start, end, warnings) = BuildDays(req, cfg);
        var (_, names) = Catalog.BuildPool(req.DepartmentIds, strict: false);
        var unitName = Units.NameOf(req.UnitId);
        if (req.UnitId is > 0 && unitName.Length == 0) throw new InvalidInputError("Die ausgewählte Abteilung existiert nicht (mehr).");
        var iso = Iso(start);
        var options = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["count"] = req.Count, ["count_max"] = req.CountMax, ["count_mode"] = req.CountMode, ["avoid_duplicates"] = req.AvoidDuplicates,
            ["even_distribution"] = req.EvenDistribution, ["avoid_recent"] = req.AvoidRecent, ["fill_hours"] = req.FillHours,
            ["school_department_ids"] = req.SchoolDepartmentIds,
        }, Json.Options).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        int? activeTemplateId;
        try { activeTemplateId = Templates.GetActive().Id; }
        catch (AppError)
        {
            activeTemplateId = null;
            warnings.Add("Es ist noch keine Vorlage aktiv – zum Erzeugen des Dokuments bitte eine Vorlage hochladen.");
        }
        var number = req.ReportNumber ?? (ComputeReportNumber(cfg.TrainingStartDate, Scheduling.WeekBounds(start).Monday) is { Length: > 0 } n ? n : NextNumber());
        var r = new ReportEntity
        {
            PeriodType = "weekly", StartDate = start, EndDate = end, CalendarWeek = iso.Week, Year = iso.Year,
            ReportNumber = number,
            TraineeName = req.TraineeName ?? cfg.TraineeName,
            TrainingYear = req.TrainingYear ?? CoverageService.ComputeTrainingYear(cfg.TrainingStartDate, start),
            DepartmentLabel = req.DepartmentLabel ?? unitName,
            UnitId = req.UnitId, DepartmentIds = req.DepartmentIds, GenerationOptions = options,
            Content = new ReportContent { Days = days }, TemplateId = activeTemplateId, Status = "draft",
        };
        Db.Reports.Add(r);
        Db.SaveChanges();
        _log.LogInformation("Generated report {Id} (KW {Week}/{Year})", r.Id, r.CalendarWeek, r.Year);
        return (r, warnings);
    }

    /// <summary>Creates a report from a week that was written by hand (nothing is generated). Existing weeks are refused.</summary>
    public (ReportEntity Report, List<string> Warnings) ImportWeek(ImportWeek req)
    {
        var cfg = SettingsSvc.Get();
        var warnings = new List<string>();
        if (req.StartDate is not { } anyDay) throw new InvalidInputError("Bitte das Datum der Woche angeben.");
        var (monday, friday) = Scheduling.WeekBounds(anyDay);
        var iso = Iso(monday);
        if (WeekInfo(monday).ExistingReportId != null)
            throw new InvalidInputError($"Für KW {iso.Week}/{iso.Year} gibt es schon einen Bericht.");
        Check.Str("department_label", req.DepartmentLabel, 0, 200);
        Check.Str("report_number", req.ReportNumber, 0, 20);

        var days = new List<ReportDay>();
        for (var i = 0; i < 5; i++)
        {
            var src = req.Days.FirstOrDefault(d => d.Weekday == i);
            var type = src?.Type ?? "free";
            Check.OneOf("day_type", type, Kinds.DayTypes);
            var entries = (src?.Entries ?? new()).Where(e => e.Text.Trim().Length > 0)
                .Select(e => new ReportEntry { Text = Kinds.Ws(e.Text), Hours = e.Hours is > 0 ? e.Hours : null }).ToList();
            if (entries.Count == 0 && type != "work" && type != "free")
                entries.Add(new ReportEntry { Text = Scheduling.DayTypes[type] });
            var total = type == "school" ? cfg.SchoolHoursPerDay : cfg.HoursPerDay;
            if (cfg.FillHours && entries.Count > 0 && type is "work" or "school" && entries.All(e => e.Hours == null))
            {
                var h = Scheduling.DistributeHours(total, entries.Count);
                for (var k = 0; k < entries.Count; k++) entries[k].Hours = h[k] > 0 ? h[k] : null;
            }
            days.Add(new ReportDay { Date = monday.AddDays(i), Type = type, Entries = entries });
        }
        if (days.All(d => d.Entries.Count == 0)) throw new InvalidInputError("Die Woche enthält keine Einträge.");

        var unit = Units.List(false).FirstOrDefault(u => u.Name.Equals(req.DepartmentLabel.Trim(), StringComparison.OrdinalIgnoreCase));
        int? templateId;
        try { templateId = Templates.GetActive().Id; }
        catch (AppError) { templateId = null; warnings.Add("Es ist noch keine Vorlage aktiv – zum Erzeugen des Dokuments bitte eine Vorlage hochladen."); }
        var number = req.ReportNumber.Trim().Length > 0 ? req.ReportNumber.Trim()
            : ComputeReportNumber(cfg.TrainingStartDate, monday) is { Length: > 0 } n ? n : NextNumber();
        var r = new ReportEntity
        {
            PeriodType = "weekly", StartDate = monday, EndDate = friday, CalendarWeek = iso.Week, Year = iso.Year, ReportNumber = number,
            TraineeName = cfg.TraineeName, TrainingYear = CoverageService.ComputeTrainingYear(cfg.TrainingStartDate, monday),
            DepartmentLabel = req.DepartmentLabel.Trim(), UnitId = unit?.Id, DepartmentIds = new(),
            GenerationOptions = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["imported"] = true }, Json.Options)
                .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
            Content = new ReportContent { Days = days }, TemplateId = templateId, Status = "draft", Remarks = req.Remarks.Trim(),
        };
        Db.Reports.Add(r);
        Db.SaveChanges();
        _log.LogInformation("Imported report {Id} (KW {Week}/{Year})", r.Id, r.CalendarWeek, r.Year);
        return (r, warnings);
    }

    /// <summary>Regenerate all 'work' entries, keeping period, day types and header fields.</summary>
    public (ReportEntity Report, List<string> Warnings) Regenerate(int reportId, int? seed = null)
    {
        var r = Get(reportId);
        if (Opt(r, "imported", false)) throw new InvalidInputError("Ein importierter Bericht kann nicht neu erzeugt werden – seine Einträge stammen von dir.");
        var cfg = SettingsSvc.Get();
        var oldDays = DaysOf(r);
        var req = new GenerateRequest
        {
            StartDate = r.StartDate, EndDate = r.EndDate, DepartmentIds = r.DepartmentIds ?? new(),
            Count = Opt(r, "count", cfg.DefaultCount), CountMax = Opt(r, "count_max", 0), CountMode = Opt(r, "count_mode", "per_week"),
            DayTypes = oldDays.ToDictionary(d => d.Date, d => d.Type),
            DaySubjects = oldDays.Where(d => d.Type == "school" && d.SubjectIds.Count > 0).ToDictionary(d => d.Date, d => d.SubjectIds),
            AvoidDuplicates = Opt(r, "avoid_duplicates", true), EvenDistribution = Opt(r, "even_distribution", true),
            AvoidRecent = Opt(r, "avoid_recent", true), FillHours = Opt(r, "fill_hours", true), Seed = seed,
            SchoolDepartmentIds = Opt<List<int>?>(r, "school_department_ids", null),
        };
        var (days, _, _, warnings) = BuildDays(req, cfg);
        SetDays(r, days);
        Db.SaveChanges();
        return (r, warnings);
    }

    /// <summary>One replacement entry for a line: school topics on school days, departments otherwise.</summary>
    public ReportEntry GenerateEntry(int reportId, EntryRequest req)
    {
        var r = Get(reportId);
        var day = DaysOf(r).FirstOrDefault(d => d.Date == req.Date);
        List<int> ids;
        if (req.School || day?.Type == "school")
        {
            ids = day is { SubjectIds.Count: > 0 } ? day.SubjectIds : Opt<List<int>?>(r, "school_department_ids", null) is { Count: > 0 } o ? o : new();
            if (ids.Count == 0) ids = Plan.DayPlan(r.StartDate, r.EndDate).Lernfelder;
            if (ids.Count == 0) ids = Catalog.ActiveIds("school");
            if (ids.Count == 0) throw new InvalidInputError("Es sind keine Lernfelder angelegt.");
        }
        else
        {
            ids = r.DepartmentIds ?? new();
            if (ids.Count == 0) throw new InvalidInputError("Dieser Bericht hat keine Themen.");
        }
        var (pool, _) = Catalog.BuildPool(ids, strict: false);
        var e = Ctx.Generator.GenerateOne(pool, req.ExcludeSnippetIds.ToHashSet(), req.PreferDepartmentId, new GenerationOptions { AvoidDuplicates = true });
        return new ReportEntry { Text = e.Text, SnippetId = e.SnippetId, DepartmentId = e.DepartmentId };
    }

    // ================================================================ edit
    private void SetDays(ReportEntity r, List<ReportDay> days)
    {
        var fresh = new ReportContent { Days = days };
        if (Json.Serialize(fresh) != Json.Serialize(r.Content))
        {
            r.Content = fresh;
            Invalidate(r);
        }
    }

    /// <summary>Changing content voids signatures - a signature must refer to what was signed.</summary>
    public void Invalidate(ReportEntity r)
    {
        if (r.Signatures.Count > 0)
        {
            Db.Signatures.RemoveRange(r.Signatures.ToList());
            r.Signatures.Clear();
        }
        if (r.Status != "draft") r.Status = "draft";
    }

    public ReportEntity Update(int reportId, ReportUpdate data)
    {
        var r = Get(reportId);
        var changed = false;
        void Patch(string? v, string cur, Action<string> set) { if (v != null && v != cur) { set(v); changed = true; } }
        Patch(data.ReportNumber, r.ReportNumber, v => r.ReportNumber = v);
        Patch(data.TraineeName, r.TraineeName, v => r.TraineeName = v);
        Patch(data.TrainingYear, r.TrainingYear, v => r.TrainingYear = v);
        Patch(data.DepartmentLabel, r.DepartmentLabel, v => r.DepartmentLabel = v);
        Patch(data.Remarks, r.Remarks, v => r.Remarks = v);
        if (changed) Invalidate(r);
        if (data.Days != null)
        {
            foreach (var d in data.Days)
                if (!(r.StartDate <= d.Date && d.Date <= r.EndDate)) throw new InvalidInputError("Ein Tag liegt außerhalb des Berichtszeitraums.");
            if (data.Days.Select(d => d.Date).Distinct().Count() != data.Days.Count) throw new InvalidInputError("Ein Tag ist doppelt vorhanden.");
            SetDays(r, data.Days.OrderBy(d => d.Date).ToList());
        }
        Db.SaveChanges();
        return r;
    }

    public void Delete(int reportId)
    {
        var r = Get(reportId);
        if (!string.IsNullOrEmpty(r.DocumentKey))
        {
            try { Ctx.Storage.DeleteDocument(r.DocumentKey); }
            catch (AppError exc) { _log.LogError(exc, "Could not delete stored document for report {Id}", reportId); }
        }
        Db.Reports.Remove(r);
        Db.SaveChanges();
    }

    // ================================================================ render / export
    public (byte[] Data, List<string> Warnings) Render(ReportEntity r)
    {
        var template = r.TemplateId != null ? Db.Templates.Find(r.TemplateId) : null;
        if (template == null)
        {
            template = Templates.GetActive();
            r.TemplateId = template.Id;
        }
        var path = Templates.PathOf(template);
        var cap = TemplateService.ToOut(template).DayCapacity;
        var sig = r.Signatures.FirstOrDefault(s => s.Role == "trainee");
        var tsig = r.Signatures.FirstOrDefault(s => s.Role == "trainer");
        var sigWarnings = new List<string>();
        var sigPng = OpenStoredSignature(r, ref sig, sigWarnings);
        var tsigPng = OpenStoredSignature(r, ref tsig, sigWarnings);
        var days = DaysOf(r);
        if (!days.SelectMany(d => d.Entries).Any(e => e.Text.Trim().Length > 0))
            throw new InvalidInputError("Der Bericht enthält keine Einträge.");
        var (values, warnings) = RenderContext.BuildValues(
            new ReportRenderInfo
            {
                TraineeName = r.TraineeName, ReportNumber = r.ReportNumber, CalendarWeek = r.CalendarWeek, Year = r.Year,
                StartDate = r.StartDate, EndDate = r.EndDate, DepartmentLabel = r.DepartmentLabel, TrainingYear = r.TrainingYear,
                Remarks = r.Remarks ?? "",
            }, days, SettingsSvc.Get(), cap,
            sigPng, sig?.SignedAt, sig?.SignerName ?? "",
            tsigPng, tsig?.SignedAt, tsig?.SignerName ?? "",
            template.LineChars > 0 ? template.LineChars : null);
        warnings.InsertRange(0, sigWarnings);
        if (sig != null && !(template.Placeholders ?? new()).Contains("SIGNATURE"))
            warnings.Add("Die Vorlage enthält keinen Platzhalter {{SIGNATURE}} – die Unterschrift erscheint nicht im Dokument.");
        try { return (DocxTemplateEngine.Render(path, values), warnings); }
        catch (AppError) { throw; }
        catch (Exception exc)
        {
            _log.LogError(exc, "Rendering report {Id} failed", r.Id);
            throw new ExportError("Das Dokument konnte nicht erzeugt werden. Details stehen im Log.");
        }
    }

    /// <summary>Decrypts the stored signature image of a report. If it cannot be decrypted (see <see cref="SignatureVault"/>) the report is
    /// rendered without it; once the attempts are used up the unreadable signature is deleted and the report is no longer "signed".</summary>
    private byte[]? OpenStoredSignature(ReportEntity r, ref SignatureEntity? sig, List<string> warnings)
    {
        if (sig == null) return null;
        var png = SettingsSvc.OpenSignature($"report:{r.Id}:{sig.Role}", sig.SignatureData, out var deleted);
        if (png != null) return png;
        if (SettingsSvc.LastSignatureProblem is { } problem) warnings.Add(problem.Message);
        if (deleted)
        {
            Db.Signatures.Remove(sig);
            r.Signatures.Remove(sig);
            if (r.Status == "signed" && r.Signatures.Count == 0) r.Status = "exported";
            Db.SaveChanges();
            sig = null;
        }
        return null;
    }

    public string DocumentFilename(ReportEntity r)
    {
        var last = r.TraineeName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var name = last.Length > 0 ? Filenames.SafeFilename(last, "") : "";
        var b = $"Berichtsheft_{r.Year}_KW{r.CalendarWeek:00}";
        if (r.ReportNumber.Length > 0) b += $"_Nr{Filenames.SafeFilename(r.ReportNumber)}";
        return name.Length > 0 ? $"{b}_{name}.docx" : $"{b}.docx";
    }

    public (ReportEntity Report, List<string> Warnings) Export(int reportId)
    {
        var r = Get(reportId);
        var (data, warnings) = Render(r);
        var stored = Ctx.Storage.UploadDocument(DocumentFilename(r), data, r.Year.ToString());
        if (!string.IsNullOrEmpty(r.DocumentKey) && r.DocumentKey != stored.Key)
        {
            try { Ctx.Storage.DeleteDocument(r.DocumentKey); }
            catch (AppError) { _log.LogWarning("Old document {Key} could not be removed", r.DocumentKey); }
        }
        r.DocumentKey = stored.Key;
        r.ExportedAt = DateTime.Now;
        r.Status = r.Signatures.Count > 0 ? "signed" : "exported";
        Db.SaveChanges();
        return (r, warnings);
    }

    public (byte[] Data, string Name) Download(int reportId)
    {
        var r = Get(reportId);
        if (string.IsNullOrEmpty(r.DocumentKey)) throw new NotFoundError("Für diesen Bericht wurde noch kein Dokument erzeugt.");
        return (Ctx.Storage.DownloadDocument(r.DocumentKey), r.DocumentKey.Split('/').Last());
    }

    public byte[] PreviewPdf(int reportId) => Ctx.Converter.ToPdf(Render(Get(reportId)).Data);

    /// <summary>The stored (signed) document of a report as PDF, exporting it first if there is none yet.</summary>
    public (byte[] Data, string Name) DownloadPdf(int reportId)
    {
        var r = Get(reportId);
        if (string.IsNullOrEmpty(r.DocumentKey) || r.Status == "draft") Export(reportId);
        var (docx, name) = Download(reportId);
        return (Ctx.Converter.ToPdf(docx), Path.ChangeExtension(name, ".pdf"));
    }

    // ================================================================ sign
    public (ReportEntity Report, List<string> Warnings) Sign(int reportId, SignRequest req)
    {
        var r = Get(reportId);
        byte[] raw;
        if (req.UseDefault && string.IsNullOrEmpty(req.Image))
        {
            raw = SettingsSvc.GetDefaultSignature() ?? throw new InvalidInputError("Es ist keine Standard-Unterschrift gespeichert.");
        }
        else raw = ImageSignature.DecodeDataUrl(req.Image ?? "");
        var cfg = SettingsSvc.Get();
        var name = !string.IsNullOrEmpty(req.SignerName) ? req.SignerName : !string.IsNullOrEmpty(r.TraineeName) ? r.TraineeName : cfg.TraineeName;
        var result = Ctx.SignatureProvider.Sign(new SignatureRequest { ReportId = r.Id, SignerName = name, ImageData = raw });
        if (req.SaveAsDefault) SettingsSvc.SetDefaultSignature(result.ImagePng);
        foreach (var old in r.Signatures.Where(s => s.Role == "trainee").ToList())
        {
            Db.Signatures.Remove(old);
            r.Signatures.Remove(old);
        }
        r.Signatures.Add(new SignatureEntity
        {
            Role = "trainee", SignerName = result.Metadata.GetValueOrDefault("signer", ""), Provider = result.Provider,
            SignatureData = SignatureVault.Protect(result.ImagePng), SignedAt = result.SignedAt,
        });
        Db.SaveChanges();
        return Export(r.Id);
    }

    public ReportEntity RemoveSignature(int reportId)
    {
        var r = Get(reportId);
        Invalidate(r);
        Db.SaveChanges();
        if (!string.IsNullOrEmpty(r.DocumentKey)) Export(r.Id);
        return r;
    }

    // ================================================================ bulk
    /// <summary>Export, sign (with the default signature) or delete several reports; failures are collected, not thrown.</summary>
    public BulkActionResult BulkAction(List<int> ids, string action)
    {
        var res = new BulkActionResult();
        foreach (var rid in ids)
        {
            try
            {
                switch (action)
                {
                    case "export": Export(rid); break;
                    case "sign":
                        if (!Get(rid).Signatures.Any(s => s.Role == "trainee")) Sign(rid, new SignRequest { UseDefault = true });
                        break;
                    case "delete": Delete(rid); break;
                }
                res.Done++;
            }
            catch (AppError exc)
            {
                Db.ChangeTracker.Clear();
                res.Failed.Add(new BulkFailure { Id = rid, Message = exc.Message });
            }
        }
        return res;
    }

    /// <summary>Plan (dry run) or create one draft per week in the range. Existing weeks are skipped.
    /// Weeks are processed chronologically so 'avoid recently used' also works across new weeks.</summary>
    public BulkResult Bulk(BulkRequest req, bool dryRun = true)
    {
        var cfg = SettingsSvc.Get();
        var depList = Catalog.ListDepartments();
        var names = depList.ToDictionary(d => d.Id, d => d.Name);
        var fallbackTopics = req.FallbackDepartmentIds.Count > 0 ? req.FallbackDepartmentIds.ToList() : Units.DefaultTopics(req.FallbackUnitId);
        var activeCounts = depList.ToDictionary(d => d.Id, d => d.ActiveSnippetCount);
        var monday = Scheduling.WeekBounds(req.StartDate).Monday;
        var lastMonday = Scheduling.WeekBounds(req.EndDate).Monday;
        var result = new BulkResult();
        if (cfg.TrainingStartDate == null)
            result.Warnings.Add("Ohne Ausbildungsbeginn (Einstellungen) wird die Nachweis-Nr. fortlaufend vergeben.");
        while (monday <= lastMonday)
        {
            var friday = monday.AddDays(4);
            var info = WeekInfo(monday);
            int? unitId; List<int> deps;
            if (info.PlanUnitId != null || info.PlanDepartmentIds.Count > 0) (unitId, deps) = (info.PlanUnitId, info.PlanDepartmentIds);
            else (unitId, deps) = (req.FallbackUnitId, fallbackTopics);
            var types = info.DayTypes;
            var workdays = types.Values.Count(t => t == "work");
            var needed = workdays > 0 ? (req.CountMode == "per_day" ? Math.Max(req.Count, req.CountMax) * workdays : req.Count) : 0;
            var available = deps.Sum(i => activeCounts.GetValueOrDefault(i));
            string action;
            if (info.ExistingReportId != null) action = "exists";
            else if (workdays > 0 && deps.Count == 0) action = "no_departments";
            else if (workdays > 0 && req.AvoidDuplicates && needed > available) action = "too_few";
            else if (workdays == 0 && !types.Values.Any(t => t == "school")) action = "no_workdays";
            else action = "create";
            var unitName = Units.NameOf(unitId);
            var week = new BulkWeek
            {
                StartDate = monday, EndDate = friday, CalendarWeek = info.CalendarWeek, Year = info.Year, ReportNumber = info.ReportNumber,
                Action = action, UnitId = unitId, DepartmentIds = deps,
                DepartmentLabel = unitName,
                TopicLabel = string.Join(", ", deps.Select(i => names.GetValueOrDefault(i, "?"))),
                SchoolDays = types.Values.Count(t => t == "school"), VacationDays = types.Values.Count(t => t == "vacation"),
                Holidays = types.Values.Count(t => t == "holiday"), ExistingReportId = info.ExistingReportId, Needed = needed, Available = available,
            };
            if (action == "create" && !dryRun)
            {
                var gen = new GenerateRequest
                {
                    StartDate = monday, UnitId = unitId, DepartmentIds = deps, Count = req.Count, CountMax = req.CountMax, CountMode = req.CountMode,
                    DayTypes = new Dictionary<DateOnly, string>(types), AvoidDuplicates = req.AvoidDuplicates,
                    EvenDistribution = req.EvenDistribution, AvoidRecent = req.AvoidRecent, FillHours = req.FillHours,
                    Seed = req.Seed == null ? null : req.Seed + result.Weeks.Count, ReportNumber = info.ReportNumber,
                };
                try
                {
                    var (r, w) = Generate(gen);
                    week.ReportId = r.Id;
                    result.Created++;
                    result.Warnings.AddRange(w.Where(x => !x.Contains("keine Vorlage")).Select(x => $"KW {week.CalendarWeek}/{week.Year}: {x}"));
                }
                catch (AppError exc)
                {
                    Db.ChangeTracker.Clear();
                    week.Action = "error";
                    result.Warnings.Add($"KW {week.CalendarWeek}/{week.Year}: {exc.Message}");
                }
            }
            result.Weeks.Add(week);
            monday = monday.AddDays(7);
        }
        return result;
    }

    /// <summary>ZIP of the documents (.docx, or converted to .pdf); missing or outdated documents are (re)generated first.</summary>
    public byte[] ZipDocuments(List<int> ids, bool pdf = false)
    {
        var files = new List<(string Name, byte[] Data)>();
        var names = new HashSet<string>();
        foreach (var rid in ids)
        {
            var r = Get(rid);
            if (string.IsNullOrEmpty(r.DocumentKey) || r.Status == "draft") Export(rid);
            var (data, name) = Download(rid);
            var folder = $"{r.Year}/";
            var n = folder + name; var i = 2;
            while (names.Contains(n)) { n = $"{folder}{name[..^5]}_{i}.docx"; i++; }
            names.Add(n);
            files.Add((n, data));
        }
        if (pdf) files = ToPdfFiles(files);
        using var buf = new MemoryStream();
        using (var z = new ZipArchive(buf, ZipArchiveMode.Create, true))
        {
            foreach (var (n, data) in files)
            {
                using var es = z.CreateEntry(n, CompressionLevel.Optimal).Open();
                es.Write(data);
            }
        }
        return buf.ToArray();
    }

    /// <summary>Converts all documents with one office start; names get the .pdf extension.</summary>
    private List<(string Name, byte[] Data)> ToPdfFiles(List<(string Name, byte[] Data)> docx)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "bh-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var inputs = new List<string>();
            for (var i = 0; i < docx.Count; i++)
            {
                var p = Path.Combine(tmp, $"{i:0000}.docx");
                File.WriteAllBytes(p, docx[i].Data);
                inputs.Add(p);
            }
            var pdfs = Ctx.Converter.ConvertManyToPdf(inputs, tmp, 60 + 10 * inputs.Count);
            return docx.Select((d, i) => (Path.ChangeExtension(d.Name, ".pdf"), File.ReadAllBytes(pdfs[i]))).ToList();
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* best effort */ } }
    }
}
