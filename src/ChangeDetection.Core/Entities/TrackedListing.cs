using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Entities;

/// <summary>
/// Tracks an individual job listing through its lifecycle.
/// Created when a new listing is first detected via ObjectDiffService.
/// </summary>
public class TrackedListing : IOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; } = Guid.Empty;

    /// <summary>The watch group this listing belongs to.</summary>
    public Guid WatchGroupId { get; set; }

    /// <summary>The watch that first detected this listing.</summary>
    public Guid SourceWatchId { get; set; }

    /// <summary>
    /// Identity key from the extraction schema (e.g., "title|company").
    /// Used for deduplication within the group.
    /// </summary>
    public required string IdentityKey { get; set; }

    /// <summary>Job title as extracted.</summary>
    public string? Title { get; set; }

    /// <summary>Company or employer name.</summary>
    public string? Company { get; set; }

    /// <summary>Job location.</summary>
    public string? Location { get; set; }

    /// <summary>Direct URL to the listing.</summary>
    public string? Url { get; set; }

    /// <summary>Application deadline, if known.</summary>
    public DateTime? Deadline { get; set; }

    /// <summary>Current lifecycle state.</summary>
    public ListingState State { get; set; } = ListingState.New;

    /// <summary>Alert level assigned by the alert policy.</summary>
    public JobAlertLevel AlertLevel { get; set; } = JobAlertLevel.Silent;

    /// <summary>Per-dimension match scores as JSON.</summary>
    public string? MatchDimensionsJson { get; set; }

    /// <summary>LLM recommendation (APPLY, REVIEW, SKIP).</summary>
    public string? Recommendation { get; set; }

    /// <summary>When this listing was first seen.</summary>
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the user was first alerted about this listing.</summary>
    public DateTime? AlertedAt { get; set; }

    /// <summary>When the user viewed this listing.</summary>
    public DateTime? SeenAt { get; set; }

    /// <summary>When the state last changed.</summary>
    public DateTime StateChangedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Reason for dismissal, if dismissed.</summary>
    public string? DismissalReason { get; set; }

    /// <summary>
    /// Number of consecutive check cycles where this listing was absent.
    /// Used to distinguish temporary page load failures from actual removal.
    /// </summary>
    public int ConsecutiveAbsences { get; set; }

    /// <summary>Why this listing expired (deadline passed, removed from source, manual).</summary>
    public ExpiryReason? ExpiryReason { get; set; }

    /// <summary>The ChangeEvent that first created this tracked listing.</summary>
    public Guid? OriginChangeEventId { get; set; }

    /// <summary>Raw extracted data as JSON for reference.</summary>
    public string? ExtractedDataJson { get; set; }

    /// <summary>Additional watch IDs that also found this listing (cross-portal dedup).</summary>
    public List<Guid> AdditionalSourceWatchIds { get; set; } = [];
}

/// <summary>
/// Why a tracked listing expired.
/// </summary>
public enum ExpiryReason
{
    /// <summary>Application deadline has passed.</summary>
    DeadlinePassed,

    /// <summary>Listing was removed from the source page (confirmed after 2+ absences).</summary>
    RemovedFromSource,

    /// <summary>Manually expired by the user.</summary>
    Manual
}

/// <summary>
/// Lifecycle states for a tracked job listing.
/// </summary>
public enum ListingState
{
    /// <summary>First detected, not yet alerted.</summary>
    New,

    /// <summary>Alert sent to user.</summary>
    Alerted,

    /// <summary>User has viewed the listing details.</summary>
    Seen,

    /// <summary>User has applied to this position.</summary>
    Applied,

    /// <summary>User dismissed this listing.</summary>
    Dismissed,

    /// <summary>Listing removed from source or deadline passed.</summary>
    Expired
}
