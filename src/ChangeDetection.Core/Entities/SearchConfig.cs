namespace ChangeDetection.Core.Entities;

/// <summary>
/// Defines how a watch acquires its content.
/// </summary>
public enum SourceType
{
    /// <summary>Standard URL-based content fetching (default).</summary>
    Url = 0,

    /// <summary>Search engine query — results are fetched via ISearchProvider.</summary>
    Search = 1
}

/// <summary>
/// Configuration for search-based watches (SourceType.Search).
/// Stored as JSON on WatchedSite.
/// </summary>
public record SearchConfig
{
    /// <summary>The search query to execute periodically.</summary>
    public required string Query { get; init; }

    /// <summary>Search provider ID (e.g., "searxng", "brave"). Null uses the default provider.</summary>
    public string? ProviderId { get; init; }

    /// <summary>Search category (e.g., "general", "news", "images").</summary>
    public string? Category { get; init; }

    /// <summary>Language filter (e.g., "en", "cs").</summary>
    public string? Language { get; init; }

    /// <summary>Time range filter (e.g., "day", "week", "month").</summary>
    public string? TimeRange { get; init; }

    /// <summary>Maximum results to return per check.</summary>
    public int MaxResults { get; init; } = 20;
}
