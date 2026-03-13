using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Entities;

/// <summary>
/// Tracks an individual extracted item through its lifecycle.
/// Domain-agnostic: works for job listings, property listings, academic papers,
/// grant opportunities, or any structured item extracted from monitored pages.
/// Created when a new item is first detected via ObjectDiffService.
/// </summary>
public class TrackedItem : IOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; } = Guid.Empty;

    /// <summary>The watch group this item belongs to.</summary>
    public Guid WatchGroupId { get; set; }

    /// <summary>The watch that first detected this item.</summary>
    public Guid SourceWatchId { get; set; }

    /// <summary>
    /// Identity key from the extraction schema (e.g., "title|company" for jobs,
    /// "address|price" for real estate). Used for deduplication within the group.
    /// </summary>
    public required string IdentityKey { get; set; }

    /// <summary>
    /// Primary display label for the item (e.g., job title, property address, paper title).
    /// Extracted from the schema's first identity field.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Secondary display label (e.g., company name, neighborhood, journal name).
    /// </summary>
    public string? DisplaySecondary { get; set; }

    /// <summary>Tertiary context label (e.g., location, year, category).</summary>
    public string? DisplayContext { get; set; }

    /// <summary>Direct URL to the item.</summary>
    public string? Url { get; set; }

    /// <summary>Deadline or expiry date, if applicable.</summary>
    public DateTime? Deadline { get; set; }

    /// <summary>Current lifecycle state.</summary>
    public TrackedItemState State { get; set; } = TrackedItemState.New;

    /// <summary>Alert level assigned by the alert policy.</summary>
    public AlertLevel AlertLevel { get; set; } = AlertLevel.Silent;

    /// <summary>Per-dimension match scores as JSON.</summary>
    public string? MatchDimensionsJson { get; set; }

    /// <summary>LLM recommendation (e.g., APPLY, REVIEW, SKIP, BUY, INVESTIGATE).</summary>
    public string? Recommendation { get; set; }

    /// <summary>When this item was first seen.</summary>
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the user was first alerted about this item.</summary>
    public DateTime? AlertedAt { get; set; }

    /// <summary>When the user viewed this item.</summary>
    public DateTime? SeenAt { get; set; }

    /// <summary>When the state last changed.</summary>
    public DateTime StateChangedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Reason for dismissal, if dismissed.</summary>
    public string? DismissalReason { get; set; }

    /// <summary>
    /// Number of consecutive check cycles where this item was absent from the source.
    /// Used to distinguish temporary page load failures from actual removal.
    /// </summary>
    public int ConsecutiveAbsences { get; set; }

    /// <summary>Why this item expired (deadline passed, removed from source, manual).</summary>
    public ExpiryReason? ExpiryReason { get; set; }

    /// <summary>The ChangeEvent that first created this tracked item.</summary>
    public Guid? OriginChangeEventId { get; set; }

    /// <summary>Raw extracted data as JSON for reference.</summary>
    public string? ExtractedDataJson { get; set; }

    /// <summary>Additional watch IDs that also found this item (cross-source dedup).</summary>
    public List<Guid> AdditionalSourceWatchIds { get; set; } = [];

    /// <summary>
    /// Domain-specific item type hint from the watch group template.
    /// E.g., "job-listing", "property", "paper", "grant". Used for UI rendering.
    /// </summary>
    public string? ItemType { get; set; }
}

/// <summary>
/// Why a tracked item expired.
/// </summary>
public enum ExpiryReason
{
    /// <summary>Deadline has passed.</summary>
    DeadlinePassed,

    /// <summary>Item was removed from the source page (confirmed after 2+ absences).</summary>
    RemovedFromSource,

    /// <summary>Manually expired by the user.</summary>
    Manual
}

/// <summary>
/// Lifecycle states for a tracked item. The core states are domain-agnostic.
/// Domain-specific actions (Applied for jobs, Contacted for real estate) are
/// represented as the ActedOn state with the action captured in metadata.
/// </summary>
public enum TrackedItemState
{
    /// <summary>First detected, not yet alerted.</summary>
    New,

    /// <summary>Alert sent to user.</summary>
    Alerted,

    /// <summary>User has viewed the item details.</summary>
    Seen,

    /// <summary>User has taken a domain-specific action (applied, contacted, bookmarked, etc.).</summary>
    ActedOn,

    /// <summary>User dismissed this item.</summary>
    Dismissed,

    /// <summary>Item removed from source or deadline passed.</summary>
    Expired
}
