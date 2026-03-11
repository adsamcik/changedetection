using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Entities;

/// <summary>
/// Represents a detected change between two snapshots.
/// </summary>
public class ChangeEvent : IOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// The ID of the user who owns this event.
    /// Guid.Empty represents the default single-user mode owner.
    /// </summary>
    public Guid OwnerId { get; set; } = Guid.Empty;
    
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

    // ========== LLM-Powered Analysis Results ==========

    /// <summary>
    /// LLM-generated semantic summary explaining what changed and why it matters.
    /// More meaningful than line-count-based summaries.
    /// </summary>
    public string? SemanticSummary { get; set; }

    /// <summary>
    /// Short one-line summary suitable for notifications.
    /// </summary>
    public string? BriefSummary { get; set; }

    /// <summary>
    /// Relevance score (0-1) based on user's original monitoring intent.
    /// Higher means change is more relevant to what user wants to monitor.
    /// </summary>
    public float? RelevanceScore { get; set; }

    /// <summary>
    /// Explanation of the relevance score.
    /// </summary>
    public string? RelevanceReason { get; set; }

    /// <summary>
    /// Semantic categories assigned to this change (JSON serialized).
    /// </summary>
    public string? CategoriesJson { get; set; }

    /// <summary>
    /// Entities extracted from the change (JSON serialized).
    /// </summary>
    public string? ExtractedEntitiesJson { get; set; }

    /// <summary>
    /// Key facts extracted from the change (JSON serialized).
    /// </summary>
    public string? KeyFactsJson { get; set; }

    /// <summary>
    /// Sentiment analysis result (JSON serialized).
    /// </summary>
    public string? SentimentJson { get; set; }

    /// <summary>
    /// Suggested actions based on the change (JSON serialized).
    /// </summary>
    public string? SuggestedActionsJson { get; set; }

    /// <summary>
    /// Whether anomalies were detected in this change.
    /// </summary>
    public bool HasAnomalies { get; set; }

    /// <summary>
    /// Anomaly score (0-1, higher = more anomalous).
    /// </summary>
    public float? AnomalyScore { get; set; }

    /// <summary>
    /// Detected anomalies (JSON serialized).
    /// </summary>
    public string? AnomaliesJson { get; set; }

    /// <summary>
    /// Overall confidence in the LLM analysis.
    /// </summary>
    public float? AnalysisConfidence { get; set; }

    /// <summary>
    /// Whether LLM analysis was performed on this change.
    /// </summary>
    public bool HasLlmAnalysis { get; set; }

    /// <summary>
    /// Per-dimension match scores when an analysis profile was used.
    /// JSON dictionary of dimension → { score, status, reason }.
    /// Populated by ChangeAnalyzer when WatchGroup has AnalysisProfileJson.
    /// </summary>
    public string? MatchDimensionsJson { get; set; }

    // ========== User Quality Feedback ==========

    /// <summary>
    /// User feedback on this change event's quality/relevance.
    /// </summary>
    public UserFeedback Feedback { get; set; } = UserFeedback.None;

    /// <summary>
    /// When the user provided feedback.
    /// </summary>
    public DateTime? FeedbackAt { get; set; }

    /// <summary>
    /// Optional user note explaining the feedback.
    /// </summary>
    public string? FeedbackNote { get; set; }
}

/// <summary>
/// User feedback on a detected change's quality.
/// </summary>
public enum UserFeedback
{
    None,
    Helpful,
    FalsePositive,
    Irrelevant,
    Missed
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
