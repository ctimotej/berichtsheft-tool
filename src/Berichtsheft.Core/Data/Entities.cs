using System.Text.Json;
using Berichtsheft.Core.Models;

namespace Berichtsheft.Core.Data;

/// <summary>Key/value setting; the value is JSON (see SettingsService).</summary>
public sealed class SettingEntity
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
}

/// <summary>UI: <i>Thema</i> (kind "topic") or <i>Fach</i>/Lernfeld (kind "school"). Table name kept from 1.0.</summary>
public sealed class DepartmentEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "topic";
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Active { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<SnippetEntity> Snippets { get; set; } = new();
}

public sealed class SnippetEntity
{
    public int Id { get; set; }
    public int DepartmentId { get; set; }
    public DepartmentEntity? Department { get; set; }
    public string Text { get; set; } = "";
    public string Category { get; set; } = "";
    public string Difficulty { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>UI: <i>Abteilung</i> – printed in the report header; suggests its default Themen.
/// <see cref="IsSchool"/> marks the Abteilung "Berufsschule": choosing it makes the week's days school days.</summary>
public sealed class UnitEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Active { get; set; } = true;
    public int SortOrder { get; set; }
    public bool IsSchool { get; set; }
    public List<int> TopicIds { get; set; } = new();
}

public sealed class TemplateEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string OriginalFilename { get; set; } = "";
    /// <summary>File name below data/templates (a UUID, never the user's name).</summary>
    public string StoredFilename { get; set; } = "";
    public List<string> Placeholders { get; set; } = new();
    public bool IsActive { get; set; }
    public int LineChars { get; set; } = 95;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class ReportEntity
{
    public int Id { get; set; }
    public string PeriodType { get; set; } = "weekly";
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
    public Dictionary<string, JsonElement> GenerationOptions { get; set; } = new();
    public ReportContent Content { get; set; } = new();
    public string Remarks { get; set; } = "";
    /// <summary>draft | exported | signed</summary>
    public string Status { get; set; } = "draft";
    public int? TemplateId { get; set; }
    public string? DocumentKey { get; set; }
    public DateTime? ExportedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<SignatureEntity> Signatures { get; set; } = new();
}

public sealed class SignatureEntity
{
    public int Id { get; set; }
    public int ReportId { get; set; }
    public ReportEntity? Report { get; set; }
    /// <summary>trainee | trainer</summary>
    public string Role { get; set; } = "trainee";
    public string SignerName { get; set; } = "";
    public string Provider { get; set; } = "";
    /// <summary>PNG, protected with <see cref="Signature.SignatureVault"/>.</summary>
    public byte[] SignatureData { get; set; } = Array.Empty<byte>();
    public DateTime SignedAt { get; set; } = DateTime.Now;
}

public sealed class PlanPeriodEntity
{
    public int Id { get; set; }
    /// <summary>rotation | school | vacation</summary>
    public string Kind { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int? UnitId { get; set; }
    public List<int> DepartmentIds { get; set; } = new();
    public string Note { get; set; } = "";
}
