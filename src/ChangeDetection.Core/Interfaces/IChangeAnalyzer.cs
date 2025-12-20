namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// LLM-powered change analysis service for advanced ingestion flow.
/// Provides semantic understanding of detected changes beyond simple diffs.
/// </summary>
public interface IChangeAnalyzer
{
    /// <summary>
    /// Analyzes a detected change to provide semantic understanding.
    /// </summary>
    /// <param name="analysisRequest">The request containing change details and context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Analysis result with semantic insights.</returns>
    Task<ChangeAnalysisResult> AnalyzeChangeAsync(
        ChangeAnalysisRequest analysisRequest,
        CancellationToken ct = default);

    /// <summary>
    /// Streams change analysis with real-time progress updates.
    /// </summary>
    IAsyncEnumerable<ChangeAnalysisProgress> AnalyzeChangeStreamingAsync(
        ChangeAnalysisRequest analysisRequest,
        CancellationToken ct = default);

    /// <summary>
    /// Detects anomalies in a change by comparing against historical patterns.
    /// </summary>
    /// <param name="request">The anomaly detection request with historical context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Anomaly detection result.</returns>
    Task<AnomalyDetectionResult> DetectAnomaliesAsync(
        AnomalyDetectionRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Request for change analysis.
/// </summary>
public class ChangeAnalysisRequest
{
    /// <summary>
    /// The raw diff content.
    /// </summary>
    public required string DiffContent { get; init; }

    /// <summary>
    /// Previous content before change.
    /// </summary>
    public string? PreviousContent { get; init; }

    /// <summary>
    /// Current content after change.
    /// </summary>
    public string? CurrentContent { get; init; }

    /// <summary>
    /// URL being monitored.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Watch name for context.
    /// </summary>
    public string? WatchName { get; init; }

    /// <summary>
    /// User's original intent when setting up the watch.
    /// </summary>
    public string? UserIntent { get; init; }

    /// <summary>
    /// Categories/tags for context.
    /// </summary>
    public List<string> Tags { get; init; } = [];

    /// <summary>
    /// Watch ID for usage tracking.
    /// </summary>
    public Guid? WatchId { get; init; }

    /// <summary>
    /// Number of lines added.
    /// </summary>
    public int LinesAdded { get; init; }

    /// <summary>
    /// Number of lines removed.
    /// </summary>
    public int LinesRemoved { get; init; }

    /// <summary>
    /// Whether to include detailed entity extraction.
    /// </summary>
    public bool ExtractEntities { get; init; } = true;

    /// <summary>
    /// Whether to detect sentiment changes.
    /// </summary>
    public bool AnalyzeSentiment { get; init; } = true;

    /// <summary>
    /// Whether to categorize the change type semantically.
    /// </summary>
    public bool CategorizeChange { get; init; } = true;
}

/// <summary>
/// Result of LLM-powered change analysis.
/// </summary>
public class ChangeAnalysisResult
{
    /// <summary>
    /// Whether analysis succeeded.
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Semantic summary of the change in natural language.
    /// More meaningful than line-count-based summaries.
    /// </summary>
    public string? SemanticSummary { get; init; }

    /// <summary>
    /// Short one-line summary suitable for notifications.
    /// </summary>
    public string? BriefSummary { get; init; }

    /// <summary>
    /// Relevance score (0-1) based on user's original intent.
    /// Higher means change is more relevant to what user wants to monitor.
    /// </summary>
    public float RelevanceScore { get; init; }

    /// <summary>
    /// Explanation of the relevance score.
    /// </summary>
    public string? RelevanceReason { get; init; }

    /// <summary>
    /// Semantic categories assigned to this change.
    /// </summary>
    public List<ChangeCategory> Categories { get; init; } = [];

    /// <summary>
    /// Entities extracted from the change (people, organizations, dates, etc.).
    /// </summary>
    public List<ExtractedEntity> ExtractedEntities { get; init; } = [];

    /// <summary>
    /// Sentiment shift detected (if applicable).
    /// </summary>
    public SentimentAnalysis? Sentiment { get; init; }

    /// <summary>
    /// Key facts or data points extracted from the change.
    /// </summary>
    public List<KeyFact> KeyFacts { get; init; } = [];

    /// <summary>
    /// Suggested actions based on the change.
    /// </summary>
    public List<string> SuggestedActions { get; init; } = [];

    /// <summary>
    /// Confidence in the overall analysis.
    /// </summary>
    public float Confidence { get; init; }

    /// <summary>
    /// Token usage for this analysis.
    /// </summary>
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
}

/// <summary>
/// Progress update during streaming analysis.
/// </summary>
public class ChangeAnalysisProgress
{
    /// <summary>
    /// Current step being processed.
    /// </summary>
    public required string Step { get; init; }

    /// <summary>
    /// Status of the step.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Thinking/reasoning content (if streaming).
    /// </summary>
    public string? ThinkingContent { get; init; }

    /// <summary>
    /// Final result (set when complete).
    /// </summary>
    public ChangeAnalysisResult? Result { get; init; }
}

/// <summary>
/// Semantic category for a change.
/// </summary>
public class ChangeCategory
{
    /// <summary>
    /// Category name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Confidence for this category.
    /// </summary>
    public float Confidence { get; init; }

    /// <summary>
    /// Brief description of why this category applies.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// An entity extracted from change content.
/// </summary>
public class ExtractedEntity
{
    /// <summary>
    /// Entity type (Person, Organization, Date, Location, Product, Price, etc.).
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Entity value.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// Whether this entity was added, removed, or modified.
    /// </summary>
    public EntityChangeType ChangeType { get; init; }

    /// <summary>
    /// Previous value if modified.
    /// </summary>
    public string? PreviousValue { get; init; }

    /// <summary>
    /// Confidence in extraction.
    /// </summary>
    public float Confidence { get; init; }
}

/// <summary>
/// Type of change to an entity.
/// </summary>
public enum EntityChangeType
{
    Added,
    Removed,
    Modified,
    Unchanged
}

/// <summary>
/// Sentiment analysis result.
/// </summary>
public class SentimentAnalysis
{
    /// <summary>
    /// Previous sentiment (Positive, Neutral, Negative).
    /// </summary>
    public string? PreviousSentiment { get; init; }

    /// <summary>
    /// Current sentiment (Positive, Neutral, Negative).
    /// </summary>
    public string? CurrentSentiment { get; init; }

    /// <summary>
    /// Sentiment score shift (-1 to 1).
    /// </summary>
    public float SentimentShift { get; init; }

    /// <summary>
    /// Description of the sentiment change.
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// A key fact extracted from the change.
/// </summary>
public class KeyFact
{
    /// <summary>
    /// The fact type (Price, Date, Quantity, Status, etc.).
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Human-readable label.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// The value.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// Previous value if changed.
    /// </summary>
    public string? PreviousValue { get; init; }

    /// <summary>
    /// Whether this is significant.
    /// </summary>
    public bool IsSignificant { get; init; }
}

/// <summary>
/// Request for anomaly detection.
/// </summary>
public class AnomalyDetectionRequest
{
    /// <summary>
    /// Current change event to analyze.
    /// </summary>
    public required ChangeAnalysisRequest CurrentChange { get; init; }

    /// <summary>
    /// Historical change summaries for pattern comparison.
    /// </summary>
    public List<HistoricalChange> HistoricalChanges { get; init; } = [];

    /// <summary>
    /// Average time between changes.
    /// </summary>
    public TimeSpan? AverageChangeInterval { get; init; }

    /// <summary>
    /// Typical change size (lines).
    /// </summary>
    public int? TypicalChangeSize { get; init; }
}

/// <summary>
/// A historical change for pattern analysis.
/// </summary>
public class HistoricalChange
{
    public DateTime DetectedAt { get; init; }
    public string? Summary { get; init; }
    public int LinesChanged { get; init; }
    public List<string> Categories { get; init; } = [];
}

/// <summary>
/// Result of anomaly detection.
/// </summary>
public class AnomalyDetectionResult
{
    /// <summary>
    /// Whether anomalies were detected.
    /// </summary>
    public bool HasAnomalies { get; init; }

    /// <summary>
    /// Anomaly score (0-1, higher = more anomalous).
    /// </summary>
    public float AnomalyScore { get; init; }

    /// <summary>
    /// Detected anomalies.
    /// </summary>
    public List<DetectedAnomaly> Anomalies { get; init; } = [];

    /// <summary>
    /// Explanation of the analysis.
    /// </summary>
    public string? Explanation { get; init; }
}

/// <summary>
/// A detected anomaly.
/// </summary>
public class DetectedAnomaly
{
    /// <summary>
    /// Type of anomaly (UnusualSize, UnusualTiming, UnexpectedContent, PatternBreak).
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Severity (Low, Medium, High).
    /// </summary>
    public required string Severity { get; init; }

    /// <summary>
    /// Description of the anomaly.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Why this is considered anomalous.
    /// </summary>
    public string? Reason { get; init; }
}
