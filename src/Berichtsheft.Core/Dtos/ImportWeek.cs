using Berichtsheft.Core.Models;

namespace Berichtsheft.Core.Dtos;

/// <summary>One day of a week that was written by hand (Weekday 0 = Monday … 4 = Friday).</summary>
public sealed class ImportDay
{
    public int Weekday { get; set; }
    public string Type { get; set; } = "work";
    public List<ReportEntry> Entries { get; set; } = new();
}

/// <summary>A week read from a Word file or typed in, before it becomes a report (see ReportService.ImportWeek).</summary>
public sealed class ImportWeek
{
    /// <summary>Any day of the week; null when it could not be read.</summary>
    public DateOnly? StartDate { get; set; }
    public string ReportNumber { get; set; } = "";
    public string DepartmentLabel { get; set; } = "";
    public string Remarks { get; set; } = "";
    public List<ImportDay> Days { get; set; } = new();
    /// <summary>File name it was read from ("" when typed in).</summary>
    public string Source { get; set; } = "";
    /// <summary>Things the user should check (unreadable date, unknown hours …).</summary>
    public List<string> Warnings { get; set; } = new();

    public int EntryCount => Days.Sum(d => d.Entries.Count(e => e.Text.Trim().Length > 0));

    /// <summary>An empty week with five work days.</summary>
    public static ImportWeek Blank(DateOnly? day = null) => new()
    {
        StartDate = day,
        Days = Enumerable.Range(0, 5).Select(i => new ImportDay { Weekday = i }).ToList(),
    };
}
