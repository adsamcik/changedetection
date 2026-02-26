using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Analyzes search result quality and suggests refined queries using LLM.
/// Works with the search pipeline to iteratively improve query effectiveness.
/// </summary>
public interface IQueryEvolutionService
{
    /// <summary>
    /// Evaluates search results and suggests improved queries.
    /// Returns null if no improvement is possible or LLM is unavailable.
    /// </summary>
    Task<QueryEvolutionResult?> EvolveQueryAsync(
        QueryEvolutionRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Request to evaluate and evolve a search query.
/// </summary>
public record QueryEvolutionRequest
{
    /// <summary>The original search query that produced these results.</summary>
    public required string OriginalQuery { get; init; }

    /// <summary>What the user is trying to find/monitor.</summary>
    public required string UserIntent { get; init; }

    /// <summary>Search results to evaluate.</summary>
    public required IReadOnlyList<SearchResult> Results { get; init; }

    /// <summary>Provider ID that produced these results.</summary>
    public string? ProviderId { get; init; }

    /// <summary>Number of previous evolution iterations (to prevent infinite loops).</summary>
    public int IterationCount { get; init; }

    /// <summary>Maximum iterations before stopping evolution.</summary>
    public int MaxIterations { get; init; } = 3;
}

/// <summary>
/// Result of query evolution analysis.
/// </summary>
public record QueryEvolutionResult
{
    /// <summary>Quality score of current results (0.0 = terrible, 1.0 = perfect).</summary>
    public required float QualityScore { get; init; }

    /// <summary>LLM's assessment of result quality.</summary>
    public required string QualityAssessment { get; init; }

    /// <summary>Suggested refined queries, ordered by expected improvement.</summary>
    public required IReadOnlyList<SuggestedQuery> SuggestedQueries { get; init; }

    /// <summary>Whether further evolution is recommended.</summary>
    public bool ShouldEvolve { get; init; }

    /// <summary>Reasoning for the evolution decision.</summary>
    public string? Reasoning { get; init; }
}

/// <summary>
/// A suggested query refinement from the LLM.
/// </summary>
public record SuggestedQuery
{
    /// <summary>The refined query text.</summary>
    public required string Query { get; init; }

    /// <summary>Why this query should produce better results.</summary>
    public required string Rationale { get; init; }

    /// <summary>Expected improvement description.</summary>
    public string? ExpectedImprovement { get; init; }

    /// <summary>Technique used (e.g., "add_quotes", "site_operator", "exclude_terms").</summary>
    public string? Technique { get; init; }
}
