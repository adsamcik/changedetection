using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Tracks job listings through their lifecycle states.
/// Handles deduplication within a watch group (same listing from multiple portals).
/// </summary>
public interface IListingTrackingService
{
    /// <summary>
    /// Process new/removed/modified listings from a diff result.
    /// Creates TrackedListings for new items, updates existing ones, marks removed as potentially expired.
    /// </summary>
    Task<ListingTrackingResult> ProcessDiffAsync(
        Guid watchGroupId,
        Guid sourceWatchId,
        Guid ownerId,
        ObjectDiffResult diffResult,
        string? matchDimensionsJson,
        string? recommendation,
        CancellationToken ct);

    /// <summary>
    /// Get all tracked listings for a watch group.
    /// </summary>
    Task<IReadOnlyList<TrackedListing>> GetListingsAsync(Guid watchGroupId, CancellationToken ct);

    /// <summary>
    /// Get tracked listings filtered by state.
    /// </summary>
    Task<IReadOnlyList<TrackedListing>> GetListingsByStateAsync(
        Guid watchGroupId, ListingState state, CancellationToken ct);

    /// <summary>
    /// Transition a listing to a new state.
    /// </summary>
    Task<bool> TransitionStateAsync(Guid listingId, ListingState newState, string? reason = null, CancellationToken ct = default);

    /// <summary>
    /// Mark a listing as viewed by the user.
    /// </summary>
    Task MarkAsSeenAsync(Guid listingId, CancellationToken ct);

    /// <summary>
    /// Expire listings whose deadline has passed.
    /// Called periodically by the background service.
    /// </summary>
    Task<int> ExpirePassedDeadlinesAsync(Guid watchGroupId, CancellationToken ct);
}

/// <summary>
/// Result of processing a diff for listing tracking.
/// </summary>
public class ListingTrackingResult
{
    /// <summary>Newly created tracked listings.</summary>
    public List<TrackedListing> NewListings { get; init; } = [];

    /// <summary>Listings that were already tracked (dedup hit from another portal).</summary>
    public List<TrackedListing> DuplicateListings { get; init; } = [];

    /// <summary>Listings marked as potentially expired (removed from source).</summary>
    public List<TrackedListing> PotentiallyExpired { get; init; } = [];

    /// <summary>Listings confirmed expired (absent for 2+ consecutive checks).</summary>
    public List<TrackedListing> ConfirmedExpired { get; init; } = [];
}


