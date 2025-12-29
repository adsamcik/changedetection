using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for tracking and persisting pipeline execution events.
/// Provides comprehensive history of all watch setup pipeline runs.
/// </summary>
public interface IPipelineEventService
{
    /// <summary>
    /// Creates a new pipeline run record when a pipeline starts.
    /// </summary>
    /// <param name="sessionId">The pipeline session ID.</param>
    /// <param name="originalInput">The original user input.</param>
    /// <param name="ownerId">The ID of the user who initiated the pipeline.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created pipeline run.</returns>
    Task<PipelineRun> StartRunAsync(
        Guid sessionId, 
        string originalInput, 
        Guid ownerId, 
        CancellationToken ct = default);
    
    /// <summary>
    /// Records an event in the pipeline execution.
    /// </summary>
    /// <param name="runId">The pipeline run ID.</param>
    /// <param name="stage">The current pipeline stage.</param>
    /// <param name="eventType">Type of event (see PipelineEventTypes).</param>
    /// <param name="summary">Human-readable summary.</param>
    /// <param name="details">Optional detailed information.</param>
    /// <param name="dataJson">Optional JSON data.</param>
    /// <param name="isSuccess">Whether this event represents success.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created event.</returns>
    Task<PipelineEvent> RecordEventAsync(
        Guid runId,
        string stage,
        string eventType,
        string? summary = null,
        string? details = null,
        string? dataJson = null,
        bool isSuccess = true,
        CancellationToken ct = default);
    
    /// <summary>
    /// Records an LLM call event with token usage.
    /// </summary>
    Task<PipelineEvent> RecordLlmCallAsync(
        Guid runId,
        string stage,
        string provider,
        string model,
        int inputTokens,
        int outputTokens,
        long durationMs,
        bool isSuccess = true,
        string? errorMessage = null,
        CancellationToken ct = default);
    
    /// <summary>
    /// Records a failure event with error details.
    /// </summary>
    Task<PipelineEvent> RecordFailureAsync(
        Guid runId,
        string stage,
        string errorMessage,
        string? stackTrace = null,
        CancellationToken ct = default);
    
    /// <summary>
    /// Records a user interaction event.
    /// </summary>
    Task<PipelineEvent> RecordUserInteractionAsync(
        Guid runId,
        string eventType,
        string? userInput,
        string? summary = null,
        CancellationToken ct = default);
    
    /// <summary>
    /// Updates the pipeline run status.
    /// </summary>
    Task UpdateRunStatusAsync(
        Guid runId,
        PipelineRunStatus status,
        string? currentStage = null,
        CancellationToken ct = default);
    
    /// <summary>
    /// Completes a pipeline run with success.
    /// </summary>
    Task CompleteRunAsync(
        Guid runId,
        Guid watchId,
        string? finalConfigurationJson = null,
        CancellationToken ct = default);
    
    /// <summary>
    /// Fails a pipeline run with an error.
    /// </summary>
    Task FailRunAsync(
        Guid runId,
        string errorMessage,
        CancellationToken ct = default);
    
    /// <summary>
    /// Cancels a pipeline run.
    /// </summary>
    Task CancelRunAsync(Guid runId, CancellationToken ct = default);
    
    /// <summary>
    /// Updates the extracted URL for a pipeline run.
    /// </summary>
    Task UpdateExtractedUrlAsync(
        Guid runId,
        string url,
        CancellationToken ct = default);
    
    /// <summary>
    /// Updates LLM usage statistics for a run.
    /// </summary>
    Task UpdateLlmUsageAsync(
        Guid runId,
        int inputTokens,
        int outputTokens,
        CancellationToken ct = default);
    
    /// <summary>
    /// Gets a pipeline run by ID.
    /// </summary>
    Task<PipelineRun?> GetRunByIdAsync(Guid runId, CancellationToken ct = default);
    
    /// <summary>
    /// Gets a pipeline run by session ID.
    /// </summary>
    Task<PipelineRun?> GetRunBySessionIdAsync(Guid sessionId, CancellationToken ct = default);
    
    /// <summary>
    /// Gets all events for a pipeline run.
    /// </summary>
    Task<IReadOnlyList<PipelineEvent>> GetEventsForRunAsync(
        Guid runId, 
        CancellationToken ct = default);
    
    /// <summary>
    /// Gets recent pipeline runs for a user.
    /// </summary>
    Task<IReadOnlyList<PipelineRun>> GetRecentRunsAsync(
        Guid ownerId,
        int count = 10,
        CancellationToken ct = default);
    
    /// <summary>
    /// Gets pipeline runs filtered by status.
    /// </summary>
    Task<IReadOnlyList<PipelineRun>> GetRunsByStatusAsync(
        Guid ownerId,
        PipelineRunStatus status,
        CancellationToken ct = default);
    
    /// <summary>
    /// Gets pipeline run statistics for a time period.
    /// </summary>
    Task<PipelineRunStatistics> GetStatisticsAsync(
        Guid ownerId,
        DateTime from,
        DateTime to,
        CancellationToken ct = default);
    
    /// <summary>
    /// Deletes old pipeline runs and their events.
    /// </summary>
    Task<int> CleanupOldRunsAsync(
        TimeSpan olderThan,
        CancellationToken ct = default);
}

/// <summary>
/// Statistics about pipeline runs.
/// </summary>
public class PipelineRunStatistics
{
    /// <summary>
    /// Total number of runs in the period.
    /// </summary>
    public int TotalRuns { get; set; }
    
    /// <summary>
    /// Number of successful runs.
    /// </summary>
    public int SuccessfulRuns { get; set; }
    
    /// <summary>
    /// Number of failed runs.
    /// </summary>
    public int FailedRuns { get; set; }
    
    /// <summary>
    /// Number of cancelled runs.
    /// </summary>
    public int CancelledRuns { get; set; }
    
    /// <summary>
    /// Average duration in milliseconds.
    /// </summary>
    public double AverageDurationMs { get; set; }
    
    /// <summary>
    /// Total LLM calls across all runs.
    /// </summary>
    public int TotalLlmCalls { get; set; }
    
    /// <summary>
    /// Total input tokens used.
    /// </summary>
    public int TotalInputTokens { get; set; }
    
    /// <summary>
    /// Total output tokens used.
    /// </summary>
    public int TotalOutputTokens { get; set; }
    
    /// <summary>
    /// Average recovery attempts per run.
    /// </summary>
    public double AverageRecoveryAttempts { get; set; }
    
    /// <summary>
    /// Success rate (0-1).
    /// </summary>
    public double SuccessRate => TotalRuns > 0 ? (double)SuccessfulRuns / TotalRuns : 0;
}
