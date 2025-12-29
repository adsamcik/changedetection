namespace ChangeDetection.Core.Entities;

/// <summary>
/// Represents a single event/step in the pipeline execution.
/// Provides detailed tracking of each stage with timing, data, and outcomes.
/// </summary>
public class PipelineEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// The pipeline run this event belongs to.
    /// </summary>
    public Guid PipelineRunId { get; set; }
    
    /// <summary>
    /// The pipeline stage this event occurred in.
    /// </summary>
    public required string Stage { get; set; }
    
    /// <summary>
    /// Type of event (Started, Progress, Completed, Failed, etc.).
    /// </summary>
    public required string EventType { get; set; }
    
    /// <summary>
    /// Sequence number within the pipeline run for ordering.
    /// </summary>
    public int SequenceNumber { get; set; }
    
    /// <summary>
    /// Human-readable summary of what happened.
    /// </summary>
    public string? Summary { get; set; }
    
    /// <summary>
    /// Detailed information about the event (e.g., extracted URL, selector).
    /// </summary>
    public string? Details { get; set; }
    
    /// <summary>
    /// JSON-serialized data specific to this event type.
    /// </summary>
    public string? DataJson { get; set; }
    
    /// <summary>
    /// Error message if this event represents a failure.
    /// </summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// Stack trace if an exception occurred.
    /// </summary>
    public string? StackTrace { get; set; }
    
    /// <summary>
    /// Whether the event was successful.
    /// </summary>
    public bool IsSuccess { get; set; } = true;
    
    /// <summary>
    /// When this event occurred.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Duration of this specific stage in milliseconds.
    /// </summary>
    public long? DurationMs { get; set; }
    
    /// <summary>
    /// LLM provider used for this event (if applicable).
    /// </summary>
    public string? LlmProvider { get; set; }
    
    /// <summary>
    /// LLM model used for this event (if applicable).
    /// </summary>
    public string? LlmModel { get; set; }
    
    /// <summary>
    /// Input tokens for LLM call (if applicable).
    /// </summary>
    public int? InputTokens { get; set; }
    
    /// <summary>
    /// Output tokens for LLM call (if applicable).
    /// </summary>
    public int? OutputTokens { get; set; }
    
    /// <summary>
    /// Confidence score (0-1) if applicable.
    /// </summary>
    public float? Confidence { get; set; }
}

/// <summary>
/// Constants for pipeline event types.
/// </summary>
public static class PipelineEventTypes
{
    public const string StageStarted = "StageStarted";
    public const string StageCompleted = "StageCompleted";
    public const string StageFailed = "StageFailed";
    public const string Progress = "Progress";
    public const string Thinking = "Thinking";
    public const string LlmCall = "LlmCall";
    public const string UserInputRequested = "UserInputRequested";
    public const string UserInputReceived = "UserInputReceived";
    public const string RecoveryStarted = "RecoveryStarted";
    public const string RecoveryCompleted = "RecoveryCompleted";
    public const string RecoveryFailed = "RecoveryFailed";
    public const string ValidationResult = "ValidationResult";
    public const string SelectorGenerated = "SelectorGenerated";
    public const string UrlExtracted = "UrlExtracted";
    public const string ContentFetched = "ContentFetched";
    public const string ContentAnalyzed = "ContentAnalyzed";
    public const string ConfigurationBuilt = "ConfigurationBuilt";
    public const string WatchCreated = "WatchCreated";
}

/// <summary>
/// Constants for pipeline stage names.
/// </summary>
public static class PipelineStageNames
{
    public const string UrlExtraction = "UrlExtraction";
    public const string ContentFetching = "ContentFetching";
    public const string ContentAnalysis = "ContentAnalysis";
    public const string SelectorGeneration = "SelectorGeneration";
    public const string SelectorValidation = "SelectorValidation";
    public const string Configuration = "Configuration";
    public const string Confirmation = "Confirmation";
    public const string Recovery = "Recovery";
}
