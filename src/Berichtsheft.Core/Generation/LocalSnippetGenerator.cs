namespace Berichtsheft.Core.Generation;

/// <summary>Random selection with even distribution over the departments (see ARCHITECTURE.md, "Generation algorithm").</summary>
public sealed class LocalSnippetGenerator : ISnippetGenerationProvider
{
    public string Name => "local";

    public GenerationResult Generate(Dictionary<int, List<SnippetCandidate>> pool, int count, GenerationOptions options)
    {
        if (count < 1) throw new NotEnoughSnippetsError("Es muss mindestens ein Eintrag erzeugt werden.");
        var rnd = options.Seed is { } seed ? new Random(seed) : new Random();
        var result = new GenerationResult();

        // 1. deduplicate by text over all departments, drop empty departments
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deps = new List<(int Id, List<SnippetCandidate> Items)>();
        foreach (var (depId, list) in pool.OrderBy(kv => kv.Key))
        {
            var items = new List<SnippetCandidate>();
            foreach (var c in list)
            {
                var key = Normalize(c.Text);
                if (key.Length > 0 && seen.Add(key)) items.Add(c);
            }
            if (items.Count == 0)
            {
                result.Warnings.Add(list.Count == 0
                    ? "Ein gewähltes Thema hat keine aktiven Textbausteine und wurde übersprungen."
                    : "Ein gewähltes Thema hat keine aktiven Textbausteine (nur doppelte Texte) und wurde übersprungen.");
                continue;
            }
            deps.Add((depId, items));
        }
        if (deps.Count == 0) throw new NotEnoughSnippetsError("Die gewählten Themen enthalten keine aktiven Textbausteine.");
        var available = deps.Sum(d => d.Items.Count);
        if (options.AvoidDuplicates && count > available)
            throw new NotEnoughSnippetsError(
                $"Es werden {count} Einträge benötigt, aber die gewählten Themen enthalten nur {available} verschiedene Textbausteine. " +
                "Bitte weitere Themen auswählen, Textbausteine ergänzen oder weniger Einträge wählen.");

        // 2. quotas
        var caps = deps.Select(d => options.AvoidDuplicates ? d.Items.Count : int.MaxValue).ToArray();
        var quotas = options.EvenDistribution ? EvenQuotas(count, caps, rnd) : WeightedQuotas(count, deps.Select(d => d.Items.Count).ToArray(), caps, rnd);
        if (options.EvenDistribution && deps.Count > 1 && quotas.Distinct().Count() > 1 && quotas.Max() - quotas.Min() > 1)
            result.Warnings.Add("Ein Thema hat zu wenige Textbausteine für eine gleichmäßige Verteilung – die anderen Themen bekommen dafür mehr Einträge.");

        // 3. pick per department, preferring snippets that were not used recently
        var perDep = new List<Queue<SnippetCandidate>>();
        for (var i = 0; i < deps.Count; i++) perDep.Add(new Queue<SnippetCandidate>(Pick(deps[i].Items, quotas[i], options.AvoidSnippetIds, rnd)));

        // 4. interleave the departments (random start order), then make sure no line repeats directly
        var order = Enumerable.Range(0, deps.Count).OrderBy(_ => rnd.Next()).ToList();
        var picked = new List<SnippetCandidate>();
        while (picked.Count < count)
        {
            var any = false;
            foreach (var i in order)
            {
                if (perDep[i].Count == 0) continue;
                picked.Add(perDep[i].Dequeue());
                any = true;
                if (picked.Count == count) break;
            }
            if (!any) break;
        }
        FixAdjacentRepeats(picked);
        result.Entries = picked.Select(c => new GeneratedEntry(c.Text, c.Id, c.DepartmentId)).ToList();
        return result;
    }

    public GeneratedEntry GenerateOne(Dictionary<int, List<SnippetCandidate>> pool, HashSet<int> excludeSnippetIds, int? preferDepartmentId, GenerationOptions options)
    {
        var rnd = options.Seed is { } seed ? new Random(seed) : new Random();
        List<SnippetCandidate> Free(IEnumerable<SnippetCandidate> list) => list.Where(c => !excludeSnippetIds.Contains(c.Id) && Normalize(c.Text).Length > 0).ToList();
        var candidates = preferDepartmentId is { } p && pool.TryGetValue(p, out var pl) ? Free(pl) : new();
        if (candidates.Count == 0) candidates = Free(pool.Values.SelectMany(v => v));
        if (candidates.Count == 0)
            throw new NotEnoughSnippetsError("Es gibt keinen weiteren passenden Textbaustein – alle werden in diesem Bericht schon verwendet.");
        var fresh = candidates.Where(c => !options.AvoidSnippetIds.Contains(c.Id)).ToList();
        var from = fresh.Count > 0 ? fresh : candidates;
        var c = from[rnd.Next(from.Count)];
        return new GeneratedEntry(c.Text, c.Id, c.DepartmentId);
    }

    private static string Normalize(string s) => string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Water-filling: everybody gets count/n; departments that cannot take their share pass the rest on.</summary>
    private static int[] EvenQuotas(int count, int[] caps, Random rnd)
    {
        var q = new int[caps.Length];
        var remaining = count;
        while (remaining > 0)
        {
            var open = Enumerable.Range(0, caps.Length).Where(i => q[i] < caps[i]).ToList();
            if (open.Count == 0) break;
            var share = remaining / open.Count;
            if (share == 0)
            {
                foreach (var i in open.OrderBy(_ => rnd.Next()).Take(remaining)) { q[i]++; remaining--; }
                continue;
            }
            foreach (var i in open)
            {
                var add = Math.Min(share, caps[i] - q[i]);
                q[i] += add;
                remaining -= add;
            }
        }
        return q;
    }

    /// <summary>Proportional to the pool size (largest remainder).</summary>
    private static int[] WeightedQuotas(int count, int[] sizes, int[] caps, Random rnd)
    {
        var total = (double)sizes.Sum();
        var exact = sizes.Select(s => count * s / total).ToArray();
        var q = exact.Select((e, i) => Math.Min(caps[i], (int)Math.Floor(e))).ToArray();
        var remaining = count - q.Sum();
        foreach (var i in Enumerable.Range(0, sizes.Length).OrderByDescending(i => exact[i] - Math.Floor(exact[i])).ThenBy(_ => rnd.Next()).ToList())
        {
            if (remaining == 0) break;
            if (q[i] < caps[i]) { q[i]++; remaining--; }
        }
        // whatever is left (caps reached) goes to departments with room
        while (remaining > 0)
        {
            var open = Enumerable.Range(0, caps.Length).Where(i => q[i] < caps[i]).ToList();
            if (open.Count == 0) break;
            q[open[rnd.Next(open.Count)]]++;
            remaining--;
        }
        return q;
    }

    /// <summary>Random order, not recently used ones first; cycles (re-shuffled) when repeats are needed.</summary>
    private static List<SnippetCandidate> Pick(List<SnippetCandidate> items, int quota, HashSet<int> avoid, Random rnd)
    {
        var outp = new List<SnippetCandidate>();
        while (outp.Count < quota)
        {
            var round = items.OrderBy(c => avoid.Contains(c.Id) ? 1 : 0).ThenBy(_ => rnd.Next()).ToList();
            if (outp.Count > 0 && round.Count > 1 && round[0].Id == outp[^1].Id) (round[0], round[1]) = (round[1], round[0]);
            outp.AddRange(round.Take(quota - outp.Count));
        }
        return outp;
    }

    private static void FixAdjacentRepeats(List<SnippetCandidate> list)
    {
        for (var i = 1; i < list.Count; i++)
        {
            if (list[i].Id != list[i - 1].Id) continue;
            for (var j = i + 1; j < list.Count; j++)
            {
                if (list[j].Id == list[i - 1].Id) continue;
                if (j + 1 < list.Count && list[j + 1].Id == list[i].Id) continue;
                (list[i], list[j]) = (list[j], list[i]);
                break;
            }
        }
    }
}
