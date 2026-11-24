namespace Berichtsheft.Core.Generation;

/// <summary>A snippet that may be picked. <see cref="Text"/> is the final line text (school topics carry the
/// "LF3: " prefix already).</summary>
public sealed record SnippetCandidate(int Id, int DepartmentId, string Text);

public sealed record GeneratedEntry(string Text, int? SnippetId, int? DepartmentId);

public sealed class GenerationOptions
{
    public bool AvoidDuplicates { get; set; } = true;
    public bool EvenDistribution { get; set; } = true;
    /// <summary>Snippets used in the last reports: only used when nothing else is left.</summary>
    public HashSet<int> AvoidSnippetIds { get; set; } = new();
    public int? Seed { get; set; }
}

public sealed class GenerationResult
{
    public List<GeneratedEntry> Entries { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>Picks report lines from a pool of snippets (pool: department id → candidates).</summary>
public interface ISnippetGenerationProvider
{
    string Name { get; }
    GenerationResult Generate(Dictionary<int, List<SnippetCandidate>> pool, int count, GenerationOptions options);
    GeneratedEntry GenerateOne(Dictionary<int, List<SnippetCandidate>> pool, HashSet<int> excludeSnippetIds, int? preferDepartmentId, GenerationOptions options);
}

public static class GenerationProviderRegistry
{
    /// <summary>Only the local generator exists; unknown names fall back to it (an AI provider would be added here).</summary>
    public static ISnippetGenerationProvider GetGenerationProvider(string? name) => name?.ToLowerInvariant() switch
    {
        _ => new LocalSnippetGenerator(),
    };
}
