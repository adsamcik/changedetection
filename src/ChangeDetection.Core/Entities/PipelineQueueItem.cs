namespace ChangeDetection.Core.Entities;

/// <summary>
/// Persistent queue item for pipeline execution.
/// Stored in LiteDB to survive server restarts.
/// </summary>
public class PipelineQueueItem
{
    /// <summary>
    /// Unique identifier for this queue item.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();
    
    /// <summary>
    /// Session ID for correlating with the conversation session.
    /// </summary>
    public required Guid SessionId { get; init; }
    
    /// <summary>
    /// User who initiated this pipeline request.
    /// </summary>
    public required Guid OwnerId { get; init; }
    
    /// <summary>
    /// The type of pipeline operation to perform.
    /// </summary>
    public required PipelineOperationType OperationType { get; init; }
    
    /// <summary>
    /// The original user input for new pipeline executions.
    /// </summary>
    public string? UserInput { get; init; }
    
    /// <summary>
    /// User feedback for continuation operations.
    /// </summary>
    public string? Feedback { get; init; }
    
    /// <summary>
    /// Serialized pipeline session JSON for continuation/recovery operations.
    /// </summary>
    public string? SessionJson { get; init; }
    
    /// <summary>
    /// Serialized pipeline options JSON.
    /// </summary>
    public string? OptionsJson { get; init; }
    
    /// <summary>
    /// Serialized failed result JSON for recovery operations.
    /// </summary>
    public string? FailedResultJson { get; init; }
    
    /// <summary>
    /// Current status of this queue item.
    /// </summary>
    public PipelineQueueStatus Status { get; set; } = PipelineQueueStatus.Pending;
    
    /// <summary>
    /// When this item was enqueued.
    /// </summary>
    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;
    
    /// <summary>
    /// When processing started (null if not yet started).
    /// </summary>
    public DateTimeOffset? StartedAt { get; set; }
    
    /// <summary>
    /// When processing completed (null if not yet completed).
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }
    
    /// <summary>
    /// Number of processing attempts.
    /// </summary>
    public int Attempts { get; set; }
    
    /// <summary>
    /// Error message if processing failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// Priority for queue ordering (lower = higher priority).
    /// </summary>
    public int Priority { get; init; }
}

/// <summary>
/// Type of pipeline operation to perform.
/// </summary>
public enum PipelineOperationType
{
    /// <summary>
    /// New pipeline execution with fresh user input.
    /// </summary>
    Process,
    
    /// <summary>
    /// Continue existing session with user feedback.
    /// </summary>
    ContinueWithFeedback,
    
    /// <summary>
    /// Attempt recovery from a failed result.
    /// </summary>
    RecoverFromFailure
}

/// <summary>
/// Status of a pipeline queue item.
/// </summary>
public enum PipelineQueueStatus
{
    /// <summary>
    /// Waiting to be picked up by a worker.
    /// </summary>
    Pending,
    
    /// <summary>
    /// Currently being processed by a worker.
    /// </summary>
    Processing,
    
    /// <summary>
    /// Successfully completed.
    /// </summary>
    Completed,
    
    /// <summary>
    /// Failed after all retry attempts.
    /// </summary>
    Failed,
    
    /// <summary>
    /// Cancelled by user or system.
    /// </summary>
    Cancelled,
    
    /// <summary>
    /// Moved to dead letter queue after exhausting all retries.
    /// Requires manual intervention to reprocess.
    /// </summary>
    DeadLetter
}
