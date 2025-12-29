using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Entities;

/// <summary>
/// Represents a complete pipeline execution for setting up a watch.
/// Tracks the overall state and outcome of the ingestion process.
/// </summary>
public class PipelineRun : IOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// The ID of the user who initiated this pipeline run.
    /// </summary>
    public Guid OwnerId { get; set; } = Guid.Empty;
    
    /// <summary>
    /// The session ID from the pipeline session.
    /// </summary>
    public Guid SessionId { get; set; }
    
    /// <summary>
    /// The original user input that started the pipeline.
    /// </summary>
    public required string OriginalInput { get; set; }
    
    /// <summary>
    /// Current status of the pipeline run.
    /// </summary>
    public PipelineRunStatus Status { get; set; } = PipelineRunStatus.Started;
    
    /// <summary>
    /// Current stage being executed.
    /// </summary>
    public string? CurrentStage { get; set; }
    
    /// <summary>
    /// URL extracted from the input (if successful).
    /// </summary>
    public string? ExtractedUrl { get; set; }
    
    /// <summary>
    /// The watch ID created from this pipeline (if successful).
    /// </summary>
    public Guid? CreatedWatchId { get; set; }
    
    /// <summary>
    /// Final error message if the pipeline failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// When the pipeline run started.
    /// </summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// When the pipeline run completed (success or failure).
    /// </summary>
    public DateTime? CompletedAt { get; set; }
    
    /// <summary>
    /// Total duration in milliseconds.
    /// </summary>
    public long? DurationMs { get; set; }
    
    /// <summary>
    /// Number of LLM calls made during this run.
    /// </summary>
    public int LlmCallCount { get; set; }
    
    /// <summary>
    /// Total input tokens used across all LLM calls.
    /// </summary>
    public int TotalInputTokens { get; set; }
    
    /// <summary>
    /// Total output tokens used across all LLM calls.
    /// </summary>
    public int TotalOutputTokens { get; set; }
    
    /// <summary>
    /// Number of recovery attempts made.
    /// </summary>
    public int RecoveryAttempts { get; set; }
    
    /// <summary>
    /// Number of user interactions (feedback/selections).
    /// </summary>
    public int UserInteractionCount { get; set; }
    
    /// <summary>
    /// JSON-serialized final configuration if successful.
    /// </summary>
    public string? FinalConfigurationJson { get; set; }
    
    /// <summary>
    /// JSON-serialized session state for debugging/recovery.
    /// </summary>
    public string? SessionStateJson { get; set; }
}

/// <summary>
/// Status of a pipeline run.
/// </summary>
public enum PipelineRunStatus
{
    /// <summary>Pipeline has been started.</summary>
    Started,
    /// <summary>Pipeline is actively processing.</summary>
    InProgress,
    /// <summary>Pipeline is waiting for user input.</summary>
    AwaitingUserInput,
    /// <summary>Pipeline completed successfully.</summary>
    Completed,
    /// <summary>Pipeline failed with an error.</summary>
    Failed,
    /// <summary>Pipeline was cancelled by the user.</summary>
    Cancelled,
    /// <summary>Pipeline is in recovery mode after a failure.</summary>
    Recovering
}
