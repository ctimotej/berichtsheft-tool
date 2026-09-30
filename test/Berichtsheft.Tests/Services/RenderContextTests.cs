using Berichtsheft.Core.Models;
using Berichtsheft.Core.Services;
using Xunit;

namespace Berichtsheft.Tests.Services;

// Ported from tests/test_features_v12.py (render context section)
public class RenderContextTests
{
    private static ReportRenderInfo Report() => new()
    {
        TraineeName = "Max",
        ReportNumber = "1",
        CalendarWeek = 40,
        Year = 2026,
        StartDate = new DateOnly(2026, 9, 28),
        EndDate = new DateOnly(2026, 10, 2),
        DepartmentLabel = "IT",
        TrainingYear = "2",
        Remarks = "",
    };

    [Fact]
    public void OverflowIsMergedIntoLastLine()
    {
        var day = new ReportDay
        {
            Date = new DateOnly(2026, 9, 28),
            Entries = Enumerable.Range(0, 4).Select(i => new ReportEntry { Text = $"E{i}", Hours = 1 }).ToList(),
        };
        var (values, warnings) = RenderContext.BuildValues(
            Report(), new List<ReportDay> { day }, new AppSettings(), new Dictionary<string, int> { ["MO"] = 3 });

        Assert.Equal("E0", values["MO_1"]);
        Assert.Equal("E2; E3", values["MO_3"]);
        Assert.Equal("2", values["MO_3_H"]);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void UnusedLinesAreBlankAndNoSignature()
    {
        var day = new ReportDay
        {
            Date = new DateOnly(2026, 9, 28),
            Entries = new List<ReportEntry> { new() { Text = "A", Hours = 8 } },
        };
        var (values, _) = RenderContext.BuildValues(
            Report(), new List<ReportDay> { day }, new AppSettings(),
            new Dictionary<string, int> { ["MO"] = 3, ["DI"] = 3 });

        Assert.Equal("", values["MO_2"]);
        Assert.Equal("", values["DI_1"]);
        Assert.Equal("", values["SIGNATURE"]);
    }
}
