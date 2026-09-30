using Berichtsheft.Core;
using Berichtsheft.Core.Generation;
using Xunit;

namespace Berichtsheft.Tests.Generation;

// Ported from tests/test_generator.py. The seed does not need to reproduce Python's exact output
// (a different RNG is used) - only the properties asserted here must hold, per docs/PORTING-NOTES.md.
public class LocalSnippetGeneratorTests
{
    private static Dictionary<int, List<SnippetCandidate>> PoolOf(Dictionary<int, int> sizes)
    {
        var pool = new Dictionary<int, List<SnippetCandidate>>();
        var sid = 1;
        foreach (var (dep, n) in sizes)
        {
            var list = new List<SnippetCandidate>();
            for (var i = 0; i < n; i++)
                list.Add(new SnippetCandidate(sid++, dep, $"Abteilung {dep} Aufgabe {i}"));
            pool[dep] = list;
        }
        return pool;
    }

    private static readonly LocalSnippetGenerator Gen = new();

    [Fact]
    public void RequestedNumberOfEntries()
    {
        var res = Gen.Generate(PoolOf(new() { [1] = 10, [2] = 10 }), 8, new GenerationOptions { Seed = 1 });
        Assert.Equal(8, res.Entries.Count);
    }

    [Fact]
    public void SingleDepartmentSelection()
    {
        var res = Gen.Generate(PoolOf(new() { [1] = 10 }), 5, new GenerationOptions { Seed = 1 });
        Assert.All(res.Entries, e => Assert.Equal(1, e.DepartmentId));
    }

    [Fact]
    public void NoDuplicates()
    {
        var res = Gen.Generate(PoolOf(new() { [1] = 5, [2] = 5 }), 10, new GenerationOptions { Seed = 3 });
        var ids = res.Entries.Select(e => e.SnippetId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void DuplicateTextsAcrossDepartmentsAreTreatedAsDuplicates()
    {
        var pool = new Dictionary<int, List<SnippetCandidate>>
        {
            [1] = new() { new SnippetCandidate(1, 1, "Dokumentation") },
            [2] = new() { new SnippetCandidate(2, 2, "Dokumentation"), new SnippetCandidate(3, 2, "Anderes") },
        };
        var res = Gen.Generate(pool, 2, new GenerationOptions { Seed = 0 });
        Assert.Equal(2, res.Entries.Select(e => e.Text).Distinct().Count());
    }

    [Fact]
    public void EvenDistribution_3_3_3()
    {
        var res = Gen.Generate(PoolOf(new() { [1] = 10, [2] = 10, [3] = 10 }), 9, new GenerationOptions { Seed = 7 });
        var counts = res.Entries.GroupBy(e => e.DepartmentId!.Value).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<int, int> { [1] = 3, [2] = 3, [3] = 3 }, counts);
    }

    [Fact]
    public void DistributionAdaptsToSmallDepartment()
    {
        var res = Gen.Generate(PoolOf(new() { [1] = 1, [2] = 10, [3] = 10 }), 9, new GenerationOptions { Seed = 7 });
        var counts = res.Entries.GroupBy(e => e.DepartmentId!.Value).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(1, counts[1]);
        Assert.Equal(8, counts[2] + counts[3]);
        Assert.True(Math.Abs(counts[2] - counts[3]) <= 1);
        Assert.NotEmpty(res.Warnings);
    }

    [Fact]
    public void EmptyDepartmentIsSkippedWithWarning()
    {
        var res = Gen.Generate(PoolOf(new() { [1] = 0, [2] = 5 }), 3, new GenerationOptions { Seed = 1 });
        Assert.Equal(3, res.Entries.Count);
        Assert.Contains(res.Warnings, w => w.Contains("keine aktiven"));
    }

    [Fact]
    public void AllDepartmentsEmptyRaises()
    {
        Assert.Throws<NotEnoughSnippetsError>(
            () => Gen.Generate(PoolOf(new() { [1] = 0, [2] = 0 }), 3, new GenerationOptions()));
    }

    [Fact]
    public void NotEnoughSnippetsRaises()
    {
        Assert.Throws<NotEnoughSnippetsError>(
            () => Gen.Generate(PoolOf(new() { [1] = 2, [2] = 2 }), 5, new GenerationOptions()));
    }

    [Fact]
    public void RepeatsAllowedWhenDuplicatesPermitted()
    {
        var res = Gen.Generate(PoolOf(new() { [1] = 2 }), 5, new GenerationOptions { AvoidDuplicates = false, Seed = 1 });
        Assert.Equal(5, res.Entries.Count);
        var ids = res.Entries.Select(e => e.SnippetId).ToList();
        for (var i = 0; i < ids.Count - 1; i++)
            Assert.NotEqual(ids[i], ids[i + 1]);
    }

    [Fact]
    public void InvalidCount()
    {
        Assert.Throws<NotEnoughSnippetsError>(
            () => Gen.Generate(PoolOf(new() { [1] = 3 }), 0, new GenerationOptions()));
    }

    [Fact]
    public void RecentSnippetsAreAvoidedWhenPossible()
    {
        var pool = PoolOf(new() { [1] = 6 });
        var recent = new HashSet<int> { 1, 2, 3 };
        var res = Gen.Generate(pool, 3, new GenerationOptions { AvoidSnippetIds = recent, Seed = 5 });
        Assert.Equal(new HashSet<int?> { 4, 5, 6 }, res.Entries.Select(e => e.SnippetId).ToHashSet());
    }

    [Fact]
    public void SeedIsDeterministic()
    {
        var p = PoolOf(new() { [1] = 10, [2] = 10 });
        var a = Gen.Generate(p, 6, new GenerationOptions { Seed = 42 });
        var b = Gen.Generate(p, 6, new GenerationOptions { Seed = 42 });
        Assert.Equal(a.Entries.Select(e => e.SnippetId), b.Entries.Select(e => e.SnippetId));
    }

    [Fact]
    public void GenerateOneExcludesUsedAndPrefersDepartment()
    {
        var p = PoolOf(new() { [1] = 3, [2] = 3 });
        var e = Gen.GenerateOne(p, new HashSet<int> { 1, 2 }, 1, new GenerationOptions { Seed = 1 });
        Assert.Equal(3, e.SnippetId);
        Assert.Equal(1, e.DepartmentId);
        Assert.Throws<NotEnoughSnippetsError>(
            () => Gen.GenerateOne(p, new HashSet<int> { 1, 2, 3, 4, 5, 6 }, null, new GenerationOptions()));
    }

    [Fact]
    public void SchedulingHelpers()
    {
        Assert.Equal((new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 2)),
            Scheduling.WeekBounds(new DateOnly(2026, 10, 1)));

        var counts = Scheduling.SplitCounts(8, 5);
        Assert.Equal(counts.OrderByDescending(x => x), counts);
        Assert.Equal(8, counts.Sum());

        var hours = Scheduling.DistributeHours(8, 3);
        Assert.Equal(8, hours.Sum());
        Assert.All(hours, h => Assert.Equal(h * 2, Math.Round(h * 2)));

        Assert.Equal("2,5", Scheduling.FormatHours(2.5));
        Assert.Equal("3", Scheduling.FormatHours(3.0));
    }

    [Fact]
    public void RangeCountsVaryBetweenMinAndMax()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var counts = Scheduling.RangeCounts(3, 6, 5, new Random(seed));
            Assert.Equal(5, counts.Count);
            Assert.All(counts, c => Assert.InRange(c, 3, 6));
            Assert.Contains(3, counts);
            Assert.Contains(6, counts);
        }
        Assert.Equal(new[] { 4, 4, 4 }, Scheduling.RangeCounts(4, 0, 3, new Random(1)));
    }
}
