namespace Berichtsheft.Core.Models;

/// <summary>One line of a report (stored as a copy inside the report, see ARCHITECTURE.md).</summary>
public sealed class ReportEntry
{
    public string Text { get; set; } = "";
    public double? Hours { get; set; }
    /// <summary>The snippet the line came from (for "avoid recently used"); null for typed lines.</summary>
    public int? SnippetId { get; set; }
    public int? DepartmentId { get; set; }
}

public sealed class ReportDay
{
    public DateOnly Date { get; set; }
    /// <summary>work | school | vacation | holiday | sick | free</summary>
    public string Type { get; set; } = "work";
    public List<ReportEntry> Entries { get; set; } = new();
    /// <summary>School days: the subjects (Lernfelder) the topics of this day come from.</summary>
    public List<int> SubjectIds { get; set; } = new();
}

/// <summary>JSON content of a report.</summary>
public sealed class ReportContent
{
    public List<ReportDay> Days { get; set; } = new();
}
