namespace ChangeDetection.Core.Entities;

/// <summary>
/// Represents a detected change between two snapshots.
/// </summary>
public class ChangeEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// The watch that detected the change.
    /// </summary>
    public Guid WatchedSiteId { get; set; }
    
    /// <summary>
    /// The previous snapshot.
    /// </summary>
    public Guid PreviousSnapshotId { get; set; }
    
    /// <summary>
    /// The current snapshot showing the change.
    /// </summary>
    public Guid CurrentSnapshotId { get; set; }
    
    /// <summary>
    /// When the change was detected.
    /// </summary>
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Summary of what changed (can be LLM-generated).
    /// </summary>
    public string? DiffSummary { get; set; }
    
    /// <summary>
    /// Raw diff data for display.
    /// </summary>
    public string? DiffHtml { get; set; }
    
    /// <summary>
    /// Type of change detected.
    /// </summary>
    public ChangeType ChangeType { get; set; }
    
    /// <summary>
    /// Importance level of the change.
    /// </summary>
    public ChangeImportance Importance { get; set; }
    
    /// <summary>
    /// Whether notifications have been sent.
    /// </summary>
    public bool IsNotified { get; set; }
    
    /// <summary>
    /// When the notification was sent.
    /// </summary>
    public DateTime? NotifiedAt { get; set; }
    
    /// <summary>
    /// Number of lines added.
    /// </summary>
    public int LinesAdded { get; set; }
    
    /// <summary>
    /// Number of lines removed.
    /// </summary>
    public int LinesRemoved { get; set; }
    
    /// <summary>
    /// Whether the user has viewed this change.
    /// </summary>
    public bool IsViewed { get; set; }

    /// <summary>
    /// Object-level diff result when schema extraction is enabled.
    /// Contains added, removed, and modified objects with field changes.
    /// </summary>
    public ObjectDiffResult? ObjectsDiff { get; set; }

    /// <summary>
    /// Filter actions that were applied to this change.
    /// </summary>
    public List<AppliedFilterAction> AppliedActions { get; set; } = [];

    /// <summary>
    /// Whether any extracted objects had ambiguous identity matches.
    /// Requires user review to resolve.
    /// </summary>
    public bool HasAmbiguousIdentities { get; set; }
}

public enum ChangeType
{
    Unknown,
    Added,
    Removed,
    Modified,
    Restructured,
    /// <summary>Schema extraction failed - requires user intervention.</summary>
    SchemaExtractionFailed,
    /// <summary>Page structure changed - schema needs re-discovery.</summary>
    SchemaDriftDetected,
    /// <summary>Multiple objects matched same identity - requires user resolution.</summary>
    AmbiguousIdentity
}
