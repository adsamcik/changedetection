using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for promoting search results into standalone URL-based watches.
/// Bridges the gap between search monitoring and page-level change detection.
/// </summary>
public interface ISearchDiscoveryService
{
    /// <summary>
    /// Promotes a search result URL into a new URL-based watch.
    /// The new watch is linked to the parent search watch via tags.
    /// </summary>
    /// <param name="parentSearchWatchId">The search watch that discovered this URL.</param>
    /// <param name="request">Details of the result to promote.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created watch, or null if promotion failed.</returns>
    Task<WatchedSite?> PromoteResultAsync(
        Guid parentSearchWatchId,
        PromoteSearchResultRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all watches that were promoted from a given search watch.
    /// </summary>
    Task<IReadOnlyList<WatchedSite>> GetPromotedWatchesAsync(
        Guid parentSearchWatchId,
        CancellationToken ct = default);
}

/// <summary>
/// Request to promote a search result into a standalone watch.
/// </summary>
public record PromoteSearchResultRequest
{
    /// <summary>URL from the search result to monitor.</summary>
    public required string Url { get; init; }

    /// <summary>Optional name for the new watch. Defaults to the search result title.</summary>
    public string? Name { get; init; }

    /// <summary>Optional CSS selector to focus monitoring on specific content.</summary>
    public string? CssSelector { get; init; }

    /// <summary>Optional check interval. Defaults to the parent watch's interval.</summary>
    public TimeSpan? CheckInterval { get; init; }
}
