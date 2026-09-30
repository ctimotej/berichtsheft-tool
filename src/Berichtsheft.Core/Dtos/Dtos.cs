using System.Text.Json.Serialization;
using Berichtsheft.Core.Models;

namespace Berichtsheft.Core.Dtos;

// Ported from app/schemas.py: contract between UI and backend, and input validation.
// JSON names are snake_case (Json.Options); day types etc. stay plain strings.
public static class Kinds
{
    public static readonly string[] DayTypes = { "work", "school", "vacation", "holiday", "sick", "free" };
    public static readonly string[] CountModes = { "per_week", "per_day" };
    public static readonly string[] DepartmentKinds = { "topic", "school" };
    public static readonly string[] PlanKinds = { "rotation", "school", "vacation" };

    public static string Ws(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static List<string> CleanTags(IEnumerable<string>? tags)
    {
        var outp = new List<string>();
        foreach (var raw in tags ?? Array.Empty<string>())
        {
            var t = (raw ?? "").Trim();
            if (t.Length > 40) t = t[..40];
            if (t.Length > 0 && !outp.Contains(t)) outp.Add(t);
        }
        return outp.Take(20).ToList();
    }
}

// ---------------------------------------------------------------- departments
public class DepartmentIn
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "topic";
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Active { get; set; } = true;
    public int SortOrder { get; set; }

    public void Validate()
    {
        Name = (Name ?? "").Trim();
        Check.Str("name", Name, 1, 120);
        Check.OneOf("kind", Kind, Kinds.DepartmentKinds);
        Check.Str("code", Code, 0, 20);
        Check.Str("description", Description, 0, 2000);
    }
}

public sealed class DepartmentOut : DepartmentIn
{
    public int Id { get; set; }
    public int SnippetCount { get; set; }
    public int ActiveSnippetCount { get; set; }
}

// ---------------------------------------------------------------- snippets
public class SnippetIn
{
    public int DepartmentId { get; set; }
    public string Text { get; set; } = "";
    public string Category { get; set; } = "";
    public string Difficulty { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public bool Active { get; set; } = true;

    public void Validate()
    {
        Check.Str("text", Text, 1, 1000);
        Text = Kinds.Ws(Text);
        if (Text.Length == 0) throw Check.Fail("text", "Text darf nicht leer sein");
        Check.Str("category", Category, 0, 120);
        Check.Str("difficulty", Difficulty, 0, 40);
        Tags = Kinds.CleanTags(Tags);
    }
}

public sealed class SnippetOut : SnippetIn
{
    public int Id { get; set; }
    public string DepartmentName { get; set; } = "";
    public string DepartmentKind { get; set; } = "topic";
    public string DepartmentCode { get; set; } = "";
}

public sealed class SnippetPatch
{
    public int? DepartmentId { get; set; }
    public string? Text { get; set; }
    public string? Category { get; set; }
    public string? Difficulty { get; set; }
    public List<string>? Tags { get; set; }
    public bool? Active { get; set; }

    public void Validate()
    {
        if (Text != null) Check.Str("text", Text, 1, 1000);
        if (Category != null) Check.Str("category", Category, 0, 120);
        if (Difficulty != null) Check.Str("difficulty", Difficulty, 0, 40);
    }
}

public sealed record ImportResult(int DepartmentsCreated, int SnippetsCreated, int SnippetsSkipped);

// ---------------------------------------------------------------- reports
public sealed class GenerateRequest
{
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int? UnitId { get; set; }
    public List<int> DepartmentIds { get; set; } = new();
    public int Count { get; set; } = 8;
    /// <summary>Upper bound of entries per day (mode "per_day" only); 0 = always exactly <see cref="Count"/>.</summary>
    public int CountMax { get; set; }
    public string CountMode { get; set; } = "per_week";
    public Dictionary<DateOnly, string> DayTypes { get; set; } = new();
    public bool AvoidDuplicates { get; set; } = true;
    public bool EvenDistribution { get; set; } = true;
    public bool AvoidRecent { get; set; } = true;
    public bool FillHours { get; set; } = true;
    public int? Seed { get; set; }
    public List<int>? SchoolDepartmentIds { get; set; }
    public Dictionary<DateOnly, List<int>> DaySubjects { get; set; } = new();
    public string? TraineeName { get; set; }
    public string? ReportNumber { get; set; }
    public string? TrainingYear { get; set; }
    public string? DepartmentLabel { get; set; }

    public void Validate()
    {
        Check.Count("department_ids", DepartmentIds, 0, 30);
        Check.Num("count", Count, 1, 100);
        if (CountMax != 0) Check.Num("count_max", CountMax, Count, 100);
        Check.OneOf("count_mode", CountMode, Kinds.CountModes);
        foreach (var t in DayTypes.Values) Check.OneOf("day_types", t, Kinds.DayTypes);
        if (SchoolDepartmentIds != null) Check.Count("school_department_ids", SchoolDepartmentIds, 0, 30);
        if (TraineeName != null) Check.Str("trainee_name", TraineeName, 0, 120);
        if (ReportNumber != null) Check.Str("report_number", ReportNumber, 0, 20);
        if (TrainingYear != null) Check.Str("training_year", TrainingYear, 0, 20);
        if (DepartmentLabel != null) Check.Str("department_label", DepartmentLabel, 0, 255);
        if (EndDate is { } end)
        {
            if (end < StartDate) throw Check.Fail("", "Das Enddatum liegt vor dem Startdatum.");
            if (end.DayNumber - StartDate.DayNumber > 13) throw Check.Fail("", "Ein Bericht darf höchstens zwei Wochen umfassen.");
        }
    }
}

public sealed class ReportUpdate
{
    public string? ReportNumber { get; set; }
    public string? TraineeName { get; set; }
    public string? TrainingYear { get; set; }
    public string? DepartmentLabel { get; set; }
    public string? Remarks { get; set; }
    public List<ReportDay>? Days { get; set; }

    public void Validate()
    {
        if (ReportNumber != null) Check.Str("report_number", ReportNumber, 0, 20);
        if (TraineeName != null) Check.Str("trainee_name", TraineeName, 0, 120);
        if (TrainingYear != null) Check.Str("training_year", TrainingYear, 0, 20);
        if (DepartmentLabel != null) Check.Str("department_label", DepartmentLabel, 0, 255);
        if (Remarks != null) Check.Str("remarks", Remarks, 0, 2000);
        if (Days != null)
        {
            Check.Count("days", Days, 0, 14);
            for (var i = 0; i < Days.Count; i++) Days[i].Validate($"days.{i}");
        }
    }
}

public static class ReportModelValidation
{
    public static void Validate(this ReportDay d, string loc)
    {
        Check.OneOf($"{loc}.type", d.Type, Kinds.DayTypes);
        Check.Count($"{loc}.entries", d.Entries, 0, 30);
        Check.Count($"{loc}.subject_ids", d.SubjectIds, 0, 20);
        for (var i = 0; i < d.Entries.Count; i++)
        {
            Check.Str($"{loc}.entries.{i}.text", d.Entries[i].Text, 0, 1000);
            Check.Num($"{loc}.entries.{i}.hours", d.Entries[i].Hours, 0, 24);
        }
    }
}

public sealed class SignatureInfo
{
    public int Id { get; set; }
    public string Role { get; set; } = "";
    public string SignerName { get; set; } = "";
    public string Provider { get; set; } = "";
    public DateTime SignedAt { get; set; }
}

public sealed class ReportOut
{
    public int Id { get; set; }
    public string PeriodType { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int CalendarWeek { get; set; }
    public int Year { get; set; }
    public string ReportNumber { get; set; } = "";
    public string TraineeName { get; set; } = "";
    public string TrainingYear { get; set; } = "";
    public string DepartmentLabel { get; set; } = "";
    public int? UnitId { get; set; }
    public List<int> DepartmentIds { get; set; } = new();
    public Dictionary<string, System.Text.Json.JsonElement> GenerationOptions { get; set; } = new();
    public List<ReportDay> Days { get; set; } = new();
    public string Remarks { get; set; } = "";
    public string Status { get; set; } = "";
    public int? TemplateId { get; set; }
    public bool HasDocument { get; set; }
    public string? DocumentName { get; set; }
    public DateTime? ExportedAt { get; set; }
    public List<SignatureInfo> Signatures { get; set; } = new();
    public double TotalHours { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ReportSummary
{
    public int Id { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int CalendarWeek { get; set; }
    public int Year { get; set; }
    public string ReportNumber { get; set; } = "";
    public string DepartmentLabel { get; set; } = "";
    public string Status { get; set; } = "";
    public int EntryCount { get; set; }
    /// <summary>Sum of all entered hours, and how many days do not match the expected working hours.</summary>
    public double TotalHours { get; set; }
    public int HoursIssues { get; set; }
    public bool HasDocument { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string TrainingYear { get; set; } = "";
    public bool Signed { get; set; }
}

// ---------------------------------------------------------------- units (Abteilungen)
public class UnitIn
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Active { get; set; } = true;
    public int SortOrder { get; set; }
    public List<int> TopicIds { get; set; } = new();

    public void Validate()
    {
        Check.Str("name", Name, 1, 120);
        Name = Kinds.Ws(Name);
        if (Name.Length == 0) throw Check.Fail("name", "Name darf nicht leer sein");
        Check.Str("description", Description, 0, 2000);
        Check.Count("topic_ids", TopicIds, 0, 50);
    }
}

public sealed class UnitOut : UnitIn
{
    public int Id { get; set; }
    public List<string> TopicNames { get; set; } = new();
}

public sealed class SchoolDayRequest
{
    public DateOnly Date { get; set; }
    public List<int> SubjectIds { get; set; } = new();
    public List<int> ExcludeSnippetIds { get; set; } = new();
    public int? Count { get; set; }

    public void Validate()
    {
        Check.Count("subject_ids", SubjectIds, 1, 20);
        Check.Count("exclude_snippet_ids", ExcludeSnippetIds, 0, 500);
        Check.Num("count", Count, 1, 10);
    }
}

public sealed class GenerateResponse
{
    public ReportOut Report { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>One replacement entry for a day of a report (pool depends on the day type).</summary>
public sealed class EntryRequest
{
    public DateOnly Date { get; set; }
    public List<int> ExcludeSnippetIds { get; set; } = new();
    public int? PreferDepartmentId { get; set; }
    public bool School { get; set; }

    public void Validate() => Check.Count("exclude_snippet_ids", ExcludeSnippetIds, 0, 500);
}

public sealed class SignRequest
{
    public string? Image { get; set; }
    public bool UseDefault { get; set; }
    public bool SaveAsDefault { get; set; }
    public string SignerName { get; set; } = "";

    public void Validate()
    {
        if (Image != null) Check.Str("image", Image, 0, 5_000_000);
        Check.Str("signer_name", SignerName, 0, 120);
        if (string.IsNullOrEmpty(Image) && !UseDefault)
            throw Check.Fail("", "Bitte unterschreibe oder verwende die gespeicherte Unterschrift.");
    }
}

public sealed class ExportResponse
{
    public ReportOut Report { get; set; } = new();
    public string DocumentName { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public List<string> Warnings { get; set; } = new();
}

public sealed class WeekInfo
{
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int CalendarWeek { get; set; }
    public int Year { get; set; }
    public string ReportNumber { get; set; } = "";
    public string TrainingYear { get; set; } = "";
    public int? ExistingReportId { get; set; }
    public Dictionary<DateOnly, string> Holidays { get; set; } = new();
    public int? PlanUnitId { get; set; }
    public Dictionary<DateOnly, string> DayTypes { get; set; } = new();
    public List<int> PlanDepartmentIds { get; set; } = new();
    public List<int> SchoolDepartmentIds { get; set; } = new();
}

// ---------------------------------------------------------------- planning
public class PlanPeriodIn
{
    public string Kind { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int? UnitId { get; set; }
    public List<int> DepartmentIds { get; set; } = new();
    public string Note { get; set; } = "";

    public void Validate()
    {
        Check.OneOf("kind", Kind, Kinds.PlanKinds);
        if (!Kinds.PlanKinds.Contains(Kind)) throw Check.Fail("kind", "Field required");
        Check.Count("department_ids", DepartmentIds, 0, 30);
        Check.Str("note", Note, 0, 255);
        if (EndDate < StartDate) throw Check.Fail("", "Das Ende liegt vor dem Beginn.");
        if (EndDate.DayNumber - StartDate.DayNumber > 800) throw Check.Fail("", "Ein Zeitraum darf höchstens gut zwei Jahre lang sein.");
        if (Kind == "rotation" && UnitId is null or 0 && DepartmentIds.Count == 0)
            throw Check.Fail("", "Bitte eine Abteilung auswählen.");
    }
}

public sealed class PlanPeriodOut : PlanPeriodIn
{
    public int Id { get; set; }
    public List<string> Labels { get; set; } = new();
    public string UnitName { get; set; } = "";
}

// ---------------------------------------------------------------- bulk generation
public sealed class BulkRequest
{
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int? FallbackUnitId { get; set; }
    public List<int> FallbackDepartmentIds { get; set; } = new();
    public int Count { get; set; } = 8;
    /// <summary>Upper bound of entries per day (mode "per_day" only); 0 = always exactly <see cref="Count"/>.</summary>
    public int CountMax { get; set; }
    public string CountMode { get; set; } = "per_week";
    public bool AvoidDuplicates { get; set; } = true;
    public bool EvenDistribution { get; set; } = true;
    public bool AvoidRecent { get; set; } = true;
    public bool FillHours { get; set; } = true;
    public int? Seed { get; set; }

    public void Validate()
    {
        Check.Count("fallback_department_ids", FallbackDepartmentIds, 0, 30);
        Check.Num("count", Count, 1, 100);
        if (CountMax != 0) Check.Num("count_max", CountMax, Count, 100);
        Check.OneOf("count_mode", CountMode, Kinds.CountModes);
        if (EndDate < StartDate) throw Check.Fail("", "Das Ende liegt vor dem Beginn.");
        if (EndDate.DayNumber - StartDate.DayNumber > 7 * 160) throw Check.Fail("", "Höchstens 160 Wochen auf einmal.");
    }
}

public sealed class BulkWeek
{
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int CalendarWeek { get; set; }
    public int Year { get; set; }
    public string ReportNumber { get; set; } = "";
    public string Action { get; set; } = "";   // create | exists | no_departments | no_workdays | too_few | error
    public int? UnitId { get; set; }
    public List<int> DepartmentIds { get; set; } = new();
    public string DepartmentLabel { get; set; } = "";
    public string TopicLabel { get; set; } = "";
    public int SchoolDays { get; set; }
    public int VacationDays { get; set; }
    public int Holidays { get; set; }
    public int? ExistingReportId { get; set; }
    public int? ReportId { get; set; }
    public int Needed { get; set; }
    public int Available { get; set; }
}

public sealed class BulkResult
{
    public List<BulkWeek> Weeks { get; set; } = new();
    public int Created { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public sealed class BulkActionRequest
{
    public List<int> Ids { get; set; } = new();
    public string Action { get; set; } = "";

    public void Validate()
    {
        Check.Count("ids", Ids, 1, 500);
        Check.OneOf("action", Action, "export", "sign", "delete");
        if (string.IsNullOrEmpty(Action)) throw Check.Fail("action", "Field required");
    }
}

public sealed class BulkFailure
{
    public int Id { get; set; }
    public string Message { get; set; } = "";
}

public sealed class BulkActionResult
{
    public int Done { get; set; }
    public List<BulkFailure> Failed { get; set; } = new();
}

public sealed class IdList
{
    public List<int> Ids { get; set; } = new();
    public void Validate()
    {
        if (Ids.Count is < 1 or > 500) throw new InvalidInputError("Bitte 1 bis 500 Berichte auswählen.");
    }
}

// ---------------------------------------------------------------- templates
public sealed class TemplateOut
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string OriginalFilename { get; set; } = "";
    public List<string> Placeholders { get; set; } = new();
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public int LineChars { get; set; } = 95;
    public Dictionary<string, int> DayCapacity { get; set; } = new();
    public List<string> UnknownPlaceholders { get; set; } = new();
}

public sealed class TemplatePatch
{
    public string? Name { get; set; }
    public int? LineChars { get; set; }

    public void Validate()
    {
        if (Name != null) Check.Str("name", Name, 1, 120);
        Check.Num("line_chars", LineChars, 20, 400);
    }
}

// ---------------------------------------------------------------- coverage (missing weeks)
public sealed class CoverageWeek
{
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int CalendarWeek { get; set; }
    public int Year { get; set; }
    public string TrainingYear { get; set; } = "";
    public string Status { get; set; } = "";  // missing | draft | exported | signed | current
    public int? ReportId { get; set; }
    public string ReportNumber { get; set; } = "";
    public bool Overdue { get; set; }
}

public sealed class CoverageOut
{
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public List<CoverageWeek> Weeks { get; set; } = new();
    public Dictionary<string, int> Counts { get; set; } = new();
    public int Missing { get; set; }
    public int Overdue { get; set; }
    public string Hint { get; set; } = "";
}

// ---------------------------------------------------------------- exam export
public sealed class ExamExportRequest
{
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public bool AutoSign { get; set; } = true;
    public string Format { get; set; } = "pdf";
    public bool IncludeCover { get; set; } = true;

    public void Validate() => Check.OneOf("format", Format, "pdf", "zip");
}

public sealed class ExamPreview
{
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int Reports { get; set; }
    public int Unsigned { get; set; }
    public int AutoSignable { get; set; }
    public int MissingWeeks { get; set; }
    public bool PdfAvailable { get; set; }
    public bool HasDefaultSignature { get; set; }
}

public sealed class SystemInfo
{
    public string Version { get; set; } = "";
    public bool LibreofficeAvailable { get; set; }
    public string StorageProvider { get; set; } = "";
    public string GenerationProvider { get; set; } = "";
    public string SignatureProvider { get; set; } = "";
    public string SignatureLegalLevel { get; set; } = "";
}
