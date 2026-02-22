namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Provider-agnostic interface for executing web searches.
/// Implementations wrap specific search engines (SearXNG, Google CSE, Brave, etc.).
/// </summary>
public interface ISearchProvider
{
    /// <summary>Unique identifier for this provider (e.g., "searxng", "google-cse", "brave").</summary>
    string ProviderId { get; }

    /// <summary>Human-readable name for display in UI.</summary>
    string DisplayName { get; }

    /// <summary>Whether this provider is currently configured and available.</summary>
    bool IsAvailable { get; }

    /// <summary>Execute a search query and return results.</summary>
    Task<SearchResultSet> SearchAsync(SearchQuery query, CancellationToken ct = default);
}

/// <summary>A search query to execute against a search provider.</summary>
public record SearchQuery
{
    public required string Query { get; init; }
    public int MaxResults { get; init; } = 20;
    public string? Category { get; init; }
    public string? Language { get; init; }
    public string? TimeRange { get; init; }
}

/// <summary>A set of search results from a provider.</summary>
public record SearchResultSet
{
    public required string ProviderId { get; init; }
    public required string Query { get; init; }
    public required IReadOnlyList<SearchResult> Results { get; init; }
    public bool IsSuccess { get; init; } = true;
    public string? ErrorMessage { get; init; }
    public long DurationMs { get; init; }
    public long? TotalResults { get; init; }
}

/// <summary>A single search result from a provider.</summary>
public record SearchResult
{
    public required string Url { get; init; }
    public required string Title { get; init; }
    public string? Snippet { get; init; }
    public string? Engine { get; init; }
    public int Position { get; init; }
    public DateTime? PublishedDate { get; init; }
    public string? Category { get; init; }
}
